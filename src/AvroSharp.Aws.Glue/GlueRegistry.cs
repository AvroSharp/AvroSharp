using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Glue;
using Amazon.Glue.Model;
using AvroSharp.Messages;
using AvroSharp.Schemas;

namespace AvroSharp.Aws.Glue;

/// <summary>
/// The Glue Schema Registry calls, as AWS's serializer makes them, with caches: a schema version's ID by its schema
/// name and definition, and a version's schema by its ID. Schema versions don't change, so nothing expires.
/// </summary>
internal sealed class GlueRegistry(IAmazonGlue glue, AvroSharpGlueOptions options) : IAvroSchemaIdResolver
{
    // AWS's serializer checks a pending version this many times.
    private const int PendingChecks = 10;

    // The registry is the authority on the schemas it holds: a writer schema with an invalid field default still
    // describes its data, and its defaults are never used to read it.
    private static readonly AvroSchemaParseOptions Lenient = new() { ValidateDefaults = false };

    private readonly ConcurrentDictionary<(string Name, string Definition), Guid> _versionIds = new();
    private readonly ConcurrentDictionary<Guid, AvroSchema> _schemas = new();
    private readonly ConditionalWeakTable<AvroSchema, string> _definitions = new();

    /// <summary>
    /// The ID of the schema version with <paramref name="schema"/>'s definition: looked up, or registered when
    /// auto-registration is on (a new version, or a new schema with its first version).
    /// </summary>
    public async ValueTask<Guid> VersionIdAsync(string schemaName, AvroSchema schema, CancellationToken cancellationToken)
    {
        // The definition is Java's Schema.toString() text, which AWS's Java and native serializers register.
        var definition = _definitions.GetValue(schema, static s => s.ToJson());
        if (_versionIds.TryGetValue((schemaName, definition), out var cached))
        {
            return cached;
        }

        Guid id;
        try
        {
            var found = await glue.GetSchemaByDefinitionAsync(
                new GetSchemaByDefinitionRequest { SchemaId = SchemaId(schemaName), SchemaDefinition = definition },
                cancellationToken).ConfigureAwait(false);
            id = found.Status == SchemaVersionStatus.AVAILABLE
                ? ParseVersionId(found.SchemaVersionId)
                : throw new InvalidOperationException($"The version of the schema '{schemaName}' with this definition is {found.Status}, not AVAILABLE.");
        }
        catch (EntityNotFoundException notFound)
        {
            if (!options.AutoRegisterSchemas)
            {
                throw new InvalidOperationException(
                    $"The registry '{options.RegistryName}' has no version of the schema '{schemaName}' with this definition, and auto-registration is off (AutoRegisterSchemas).",
                    notFound);
            }

            id = await RegisterAsync(schemaName, definition, cancellationToken).ConfigureAwait(false);
        }

        _schemas.TryAdd(id, schema);
        return _versionIds.GetOrAdd((schemaName, definition), id);
    }

    public AvroSchema? GetSchema(AvroSchemaId id) =>
        _schemas.TryGetValue(id.Guid, out var schema)
            ? schema
            : throw new NotSupportedException("The AWS SDK for .NET calls Glue asynchronously only: read with the asynchronous methods.");

    public async ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default)
    {
        if (_schemas.TryGetValue(id.Guid, out var cached))
        {
            return cached;
        }

        var version = await glue.GetSchemaVersionAsync(new GetSchemaVersionRequest { SchemaVersionId = id.Guid.ToString() }, cancellationToken).ConfigureAwait(false);
        if (version.DataFormat != DataFormat.AVRO)
        {
            throw new InvalidOperationException($"The schema version {id.Guid} is {version.DataFormat}, not AVRO.");
        }

        return _schemas.GetOrAdd(id.Guid, AvroSchema.Parse(version.SchemaDefinition, Lenient));
    }

    // A new version of the schema or, when the schema doesn't exist, the schema with this as its first version.
    private async Task<Guid> RegisterAsync(string schemaName, string definition, CancellationToken cancellationToken)
    {
        string versionId;
        SchemaVersionStatus status;
        try
        {
            (versionId, status) = await RegisterVersionAsync(schemaName, definition, cancellationToken).ConfigureAwait(false);
        }
        catch (EntityNotFoundException)
        {
            try
            {
                var created = await glue.CreateSchemaAsync(
                    new CreateSchemaRequest
                    {
                        RegistryId = new RegistryId { RegistryName = options.RegistryName },
                        SchemaName = schemaName,
                        DataFormat = DataFormat.AVRO,
                        Compatibility = options.Compatibility,
                        SchemaDefinition = definition,
                        Description = options.Description,
                        Tags = options.Tags is null ? null : new Dictionary<string, string>(options.Tags, StringComparer.Ordinal),
                    },
                    cancellationToken).ConfigureAwait(false);
                (versionId, status) = (created.SchemaVersionId, created.SchemaVersionStatus);
            }
            catch (AlreadyExistsException)
            {
                // Another producer created the schema meanwhile: add the version to it.
                (versionId, status) = await RegisterVersionAsync(schemaName, definition, cancellationToken).ConfigureAwait(false);
            }
        }

        return await AvailableAsync(versionId, status, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string VersionId, SchemaVersionStatus Status)> RegisterVersionAsync(string schemaName, string definition, CancellationToken cancellationToken)
    {
        var registered = await glue.RegisterSchemaVersionAsync(
            new RegisterSchemaVersionRequest { SchemaId = SchemaId(schemaName), SchemaDefinition = definition },
            cancellationToken).ConfigureAwait(false);
        return (registered.SchemaVersionId, registered.Status);
    }

    // A new version is PENDING while the registry checks its compatibility, then AVAILABLE, or FAILURE. It is
    // checked every PendingVersionInterval, PendingChecks times at most.
    private async Task<Guid> AvailableAsync(string versionId, SchemaVersionStatus status, CancellationToken cancellationToken)
    {
        var id = ParseVersionId(versionId);
        for (var check = 0; status == SchemaVersionStatus.PENDING && check < PendingChecks; check++)
        {
            await Task.Delay(options.PendingVersionInterval, cancellationToken).ConfigureAwait(false);
            status = (await glue.GetSchemaVersionAsync(new GetSchemaVersionRequest { SchemaVersionId = versionId }, cancellationToken).ConfigureAwait(false)).Status;
        }

        return status == SchemaVersionStatus.AVAILABLE
            ? id
            : status == SchemaVersionStatus.PENDING
                ? throw new TimeoutException($"The schema version {versionId} is still PENDING after {PendingChecks} checks, {options.PendingVersionInterval} apart: the registry hasn't finished its compatibility check.")
                : throw new InvalidOperationException($"The registry didn't make the schema version {versionId} available: it is {status}. Its compatibility check may have failed.");
    }

    private static Guid ParseVersionId(string? versionId) =>
        Guid.TryParse(versionId, out var id) ? id : throw new InvalidOperationException($"The registry answered without a valid schema version ID ('{versionId}').");

    private SchemaId SchemaId(string schemaName) => new() { RegistryName = options.RegistryName, SchemaName = schemaName };
}
