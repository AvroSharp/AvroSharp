using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Glue;
using Amazon.Glue.Model;
using Amazon.Runtime;

namespace AvroSharp.Aws.Glue.Tests;

/// <summary>
/// Glue Schema Registry in memory, through the SDK client's virtual methods. It answers as the service does: a missing
/// schema and a missing version are <see cref="EntityNotFoundException"/>s (with the messages AWS's serializer looks
/// for), and a new version can stay <c>PENDING</c> for a number of checks, or fail its compatibility check.
/// </summary>
internal sealed class InMemoryGlueClient() : AmazonGlueClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.USEast1)
{
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<(string Registry, string Schema), List<Version>> _schemas = [];
    private readonly Dictionary<string, Version> _versions = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many status checks a new version (not a schema's first) stays PENDING for.</summary>
    public int PendingChecks { get; set; }

    /// <summary>Gets or sets whether new versions (not a schema's first) fail their compatibility check.</summary>
    public bool FailNewVersions { get; set; }

    /// <summary>Gets or sets whether another producer creates each schema just before this client's CreateSchema call.</summary>
    public bool CreatedElsewhere { get; set; }

    /// <summary>Gets or sets whether GetSchemaByDefinition answers without a schema version ID.</summary>
    public bool WithoutVersionIds { get; set; }

    public List<string> Calls { get; } = [];

    public List<CreateSchemaRequest> Created { get; } = [];

    /// <summary>Adds a schema version as another producer would, of any data format.</summary>
    public string Add(string schemaName, string definition, DataFormat? format = null, string registry = "default-registry", string? id = null)
    {
        lock (_lock)
        {
            return AddVersion(registry, schemaName, definition, format ?? DataFormat.AVRO, SchemaVersionStatus.AVAILABLE, id).Id;
        }
    }

    public override Task<GetSchemaByDefinitionResponse> GetSchemaByDefinitionAsync(GetSchemaByDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add("GetSchemaByDefinition");
            if (!_schemas.TryGetValue((request.SchemaId.RegistryName, request.SchemaId.SchemaName), out var versions))
            {
                throw new EntityNotFoundException("Schema is not found.");
            }

            var version = versions.Find(v => string.Equals(v.Definition, request.SchemaDefinition, StringComparison.Ordinal))
                ?? throw new EntityNotFoundException("Schema version is not found.");
            return Task.FromResult(new GetSchemaByDefinitionResponse { SchemaVersionId = WithoutVersionIds ? null : version.Id, Status = version.Status, DataFormat = version.Format });
        }
    }

    public override Task<RegisterSchemaVersionResponse> RegisterSchemaVersionAsync(RegisterSchemaVersionRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add("RegisterSchemaVersion");
            if (!_schemas.TryGetValue((request.SchemaId.RegistryName, request.SchemaId.SchemaName), out var versions))
            {
                throw new EntityNotFoundException("Schema is not found.");
            }

            var version = versions.Find(v => string.Equals(v.Definition, request.SchemaDefinition, StringComparison.Ordinal))
                ?? AddVersion(request.SchemaId.RegistryName, request.SchemaId.SchemaName, request.SchemaDefinition, DataFormat.AVRO, SchemaVersionStatus.PENDING);
            if (version.Status == SchemaVersionStatus.PENDING && PendingChecks == 0)
            {
                version.Status = FailNewVersions ? SchemaVersionStatus.FAILURE : SchemaVersionStatus.AVAILABLE;
            }

            version.ChecksLeft = PendingChecks;
            return Task.FromResult(new RegisterSchemaVersionResponse { SchemaVersionId = version.Id, Status = version.Status, VersionNumber = version.Number });
        }
    }

    public override Task<CreateSchemaResponse> CreateSchemaAsync(CreateSchemaRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add("CreateSchema");
            if (CreatedElsewhere)
            {
                AddVersion(request.RegistryId.RegistryName, request.SchemaName, """{"type":"record","name":"Other","fields":[]}""", DataFormat.AVRO, SchemaVersionStatus.AVAILABLE);
            }

            if (_schemas.ContainsKey((request.RegistryId.RegistryName, request.SchemaName)))
            {
                throw new AlreadyExistsException($"The schema {request.SchemaName} exists.");
            }

            Created.Add(request);
            var version = AddVersion(request.RegistryId.RegistryName, request.SchemaName, request.SchemaDefinition, request.DataFormat, SchemaVersionStatus.AVAILABLE);
            return Task.FromResult(new CreateSchemaResponse { SchemaName = request.SchemaName, SchemaVersionId = version.Id, SchemaVersionStatus = version.Status, Compatibility = request.Compatibility });
        }
    }

    public override Task<GetSchemaVersionResponse> GetSchemaVersionAsync(GetSchemaVersionRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add("GetSchemaVersion");
            var version = _versions.TryGetValue(request.SchemaVersionId, out var found) ? found : throw new EntityNotFoundException("Schema version is not found.");
            if (version.Status == SchemaVersionStatus.PENDING && --version.ChecksLeft <= 0)
            {
                version.Status = FailNewVersions ? SchemaVersionStatus.FAILURE : SchemaVersionStatus.AVAILABLE;
            }

            return Task.FromResult(new GetSchemaVersionResponse
            {
                SchemaVersionId = version.Id,
                SchemaDefinition = version.Definition,
                DataFormat = version.Format,
                Status = version.Status,
                VersionNumber = version.Number,
            });
        }
    }

    private Version AddVersion(string registry, string schemaName, string definition, DataFormat format, SchemaVersionStatus status, string? id = null)
    {
        if (!_schemas.TryGetValue((registry, schemaName), out var versions))
        {
            versions = [];
            _schemas[(registry, schemaName)] = versions;
        }

        var version = new Version(id ?? Guid.NewGuid().ToString(), definition, format, versions.Count + 1) { Status = status };
        versions.Add(version);
        _versions[version.Id] = version;
        return version;
    }

    public int Count(string call) => Calls.Count(c => string.Equals(c, call, StringComparison.Ordinal));

    private sealed class Version(string id, string definition, DataFormat format, long number)
    {
        public string Id { get; } = id;

        public string Definition { get; } = definition;

        public DataFormat Format { get; } = format;

        public long Number { get; } = number;

        public SchemaVersionStatus Status { get; set; } = SchemaVersionStatus.AVAILABLE;

        public int ChecksLeft { get; set; }
    }
}
