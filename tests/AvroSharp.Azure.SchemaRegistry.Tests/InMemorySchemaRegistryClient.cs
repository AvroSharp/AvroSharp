using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Data.SchemaRegistry;

namespace AvroSharp.Azure.SchemaRegistry.Tests;

/// <summary>
/// Azure Schema Registry in memory, through the client's protected constructor and virtual methods, as the Azure SDK
/// intends for tests. A schema is the same as a registered one only when its group, name and text are: the service's
/// own comparison isn't documented, and this one doesn't hide a difference in text.
/// </summary>
internal sealed class InMemorySchemaRegistryClient : SchemaRegistryClient
{
    // The HTTP response under every value: there is none, and disposing it does nothing.
    private static readonly Response Ok = new EmptyResponse();

    private readonly Dictionary<string, SchemaRegistrySchema> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Group, string Name, string Definition), string> _ids = [];
    private readonly Dictionary<(string Group, string Name), int> _versions = [];

    public int RegistrationCalls { get; private set; }

    public int LookupCalls { get; private set; }

    public int GetSchemaCalls { get; private set; }

    public override Response<SchemaProperties> RegisterSchema(string groupName, string schemaName, string schemaDefinition, SchemaFormat format, CancellationToken cancellationToken = default)
    {
        lock (_byId)
        {
            RegistrationCalls++;
            if (!_ids.TryGetValue((groupName, schemaName, schemaDefinition), out var id))
            {
                var version = _versions.TryGetValue((groupName, schemaName), out var last) ? last + 1 : 1;
                _versions[(groupName, schemaName)] = version;
                id = Guid.NewGuid().ToString("N");
                _ids[(groupName, schemaName, schemaDefinition)] = id;
                _byId[id] = SchemaRegistryModelFactory.SchemaRegistrySchema(SchemaRegistryModelFactory.SchemaProperties(format, id, groupName, schemaName), schemaDefinition);
            }

            return Response.FromValue(_byId[id].Properties, Ok);
        }
    }

    public override Task<Response<SchemaProperties>> RegisterSchemaAsync(string groupName, string schemaName, string schemaDefinition, SchemaFormat format, CancellationToken cancellationToken = default) =>
        Task.FromResult(RegisterSchema(groupName, schemaName, schemaDefinition, format, cancellationToken));

    public override Response<SchemaProperties> GetSchemaProperties(string groupName, string schemaName, string schemaDefinition, SchemaFormat format, CancellationToken cancellationToken = default)
    {
        lock (_byId)
        {
            LookupCalls++;
            return _ids.TryGetValue((groupName, schemaName, schemaDefinition), out var id)
                ? Response.FromValue(_byId[id].Properties, Ok)
                : throw new RequestFailedException(404, $"The schema {schemaName} is not in the group {groupName}.");
        }
    }

    public override Task<Response<SchemaProperties>> GetSchemaPropertiesAsync(string groupName, string schemaName, string schemaDefinition, SchemaFormat format, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetSchemaProperties(groupName, schemaName, schemaDefinition, format, cancellationToken));

    public override Response<SchemaRegistrySchema> GetSchema(string schemaId, CancellationToken cancellationToken = default)
    {
        lock (_byId)
        {
            GetSchemaCalls++;
            return _byId.TryGetValue(schemaId, out var schema)
                ? Response.FromValue(schema, Ok)
                : throw new RequestFailedException(404, $"No schema has the ID {schemaId}.");
        }
    }

    public override Task<Response<SchemaRegistrySchema>> GetSchemaAsync(string schemaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetSchema(schemaId, cancellationToken));

    /// <summary>The HTTP response under a value: none, in memory.</summary>
    private sealed class EmptyResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = "";

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
