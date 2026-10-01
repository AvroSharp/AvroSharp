using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// A schema registry in memory, for the serde tests. As in a real registry, a schema equivalent to a registered one
/// (the same schema, whatever its JSON formatting) is that one: the same version in the subject, and the same ID in
/// every subject.
/// </summary>
internal sealed class InMemorySchemaRegistry : ISchemaRegistryClient
{
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly List<RegisteredSchema> _byId = [];
    private readonly Dictionary<string, List<RegisteredSchema>> _bySubject = new(StringComparer.Ordinal);
    private int _lastId;

    /// <summary>The number of calls that registered a schema or looked one up, to check the serializers cache IDs.</summary>
    public int RegistrationCalls { get; private set; }

    public IEnumerable<KeyValuePair<string, string>> Config => [];

    public IAuthenticationHeaderValueProvider AuthHeaderProvider => null!;

    public IWebProxy Proxy => null!;

    public int MaxCachedSchemas => 1000;

    public Task<int> RegisterSchemaAsync(string subject, Schema schema, bool normalize = false) =>
        RegisterSchemaWithResponseAsync(subject, schema, normalize).ContinueWith(t => t.Result.Id, TaskScheduler.Default);

    public Task<int> RegisterSchemaAsync(string subject, string avroSchema, bool normalize = false) =>
        RegisterSchemaAsync(subject, new Schema(avroSchema, SchemaType.Avro), normalize);

    public Task<RegisteredSchema> RegisterSchemaWithResponseAsync(string subject, Schema schema, bool normalize = false)
    {
        lock (_lock)
        {
            RegistrationCalls++;
            var versions = Versions(subject);
            if (versions.Find(s => SameSchema(s.SchemaString, schema.SchemaString)) is { } existing)
            {
                return Task.FromResult(existing);
            }

            var id = _byId.Find(s => SameSchema(s.SchemaString, schema.SchemaString))?.Id ?? ++_lastId;
            var registered = new RegisteredSchema(subject, versions.Count + 1, id, Guid.NewGuid().ToString(), schema.SchemaString, schema.SchemaType, schema.References ?? [])
            {
                Metadata = schema.Metadata,
                RuleSet = schema.RuleSet,
            };
            versions.Add(registered);
            _byId.Add(registered);
            return Task.FromResult(registered);
        }
    }

    public Task<RegisteredSchema> LookupSchemaAsync(string subject, Schema schema, bool ignoreDeletedSchemas, bool normalize = false)
    {
        lock (_lock)
        {
            RegistrationCalls++;
            return Versions(subject).Find(s => SameSchema(s.SchemaString, schema.SchemaString)) is { } found
                ? Task.FromResult(found)
                : throw NotFound($"The schema is not registered under '{subject}'.");
        }
    }

    public Task<Schema> GetSchemaAsync(int id, string format = null!)
    {
        lock (_lock)
        {
            return Task.FromResult(ById(id).Schema);
        }
    }

    public Task<Schema> GetSchemaBySubjectAndIdAsync(string subject, int id, string format = null!) => GetSchemaAsync(id, format);

    public Task<Schema> GetSchemaByGuidAsync(string guid, string format = null!)
    {
        lock (_lock)
        {
            return _byId.Find(s => string.Equals(s.Guid, guid, StringComparison.Ordinal)) is { } found ? Task.FromResult(found.Schema) : throw NotFound($"No schema has the GUID {guid}.");
        }
    }

    public Task<string> GetSchemaAsync(string subject, int version) =>
        GetRegisteredSchemaAsync(subject, version).ContinueWith(t => t.Result.SchemaString, TaskScheduler.Default);

    public Task<RegisteredSchema> GetRegisteredSchemaAsync(string subject, int version, bool ignoreDeletedSchemas = true)
    {
        lock (_lock)
        {
            var versions = Versions(subject);
            return version >= 1 && version <= versions.Count ? Task.FromResult(versions[version - 1]) : throw NotFound($"'{subject}' has no version {version}.");
        }
    }

    public Task<RegisteredSchema> GetLatestSchemaAsync(string subject)
    {
        lock (_lock)
        {
            var versions = Versions(subject);
            return versions.Count > 0 ? Task.FromResult(versions[^1]) : throw NotFound($"'{subject}' has no versions.");
        }
    }

    public Task<RegisteredSchema> GetLatestWithMetadataAsync(string subject, IDictionary<string, string> metadata, bool ignoreDeletedSchemas)
    {
        lock (_lock)
        {
            var match = Versions(subject).LastOrDefault(s => metadata.All(m => s.Metadata?.Properties is { } p && p.TryGetValue(m.Key, out var v) && string.Equals(v, m.Value, StringComparison.Ordinal)));
            return match is not null ? Task.FromResult(match) : throw NotFound($"No version of '{subject}' has the metadata.");
        }
    }

    public Task<List<int>> GetSubjectVersionsAsync(string subject)
    {
        lock (_lock)
        {
            return Task.FromResult(Versions(subject).Select(s => s.Version).ToList());
        }
    }

    public Task<List<string>> GetAllSubjectsAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_bySubject.Where(s => s.Value.Count > 0).Select(s => s.Key).ToList());
        }
    }

    public Task<int> GetSchemaIdAsync(string subject, Schema schema, bool normalize = false) =>
        LookupSchemaAsync(subject, schema, ignoreDeletedSchemas: true, normalize).ContinueWith(t => t.Result.Id, TaskScheduler.Default);

    public Task<int> GetSchemaIdAsync(string subject, string avroSchema, bool normalize = false) =>
        GetSchemaIdAsync(subject, new Schema(avroSchema, SchemaType.Avro), normalize);

    public string ConstructKeySubjectName(string topic, string recordType = null!) => topic + "-key";

    public string ConstructValueSubjectName(string topic, string recordType = null!) => topic + "-value";

    public Task<bool> IsCompatibleAsync(string subject, Schema schema) => throw new NotSupportedException();

    public Task<bool> IsCompatibleAsync(string subject, string avroSchema) => throw new NotSupportedException();

    public Task<Compatibility> GetCompatibilityAsync(string subject = null!) => throw new NotSupportedException();

    public Task<Compatibility> UpdateCompatibilityAsync(Compatibility compatibility, string subject = null!) => throw new NotSupportedException();

    public Task<AssociationResponse> CreateAssociationAsync(AssociationCreateOrUpdateRequest request) => throw new NotSupportedException();

    public Task<List<Association>> GetAssociationsByResourceNameAsync(string resourceName, string resourceNamespace, string resourceType, List<string> associationTypes, string lifecycle, int offset, int limit) =>
        Task.FromResult(new List<Association>());

    public Task DeleteAssociationsAsync(string resourceId, string resourceType, List<string> associationTypes, bool cascadeLifecycle) => throw new NotSupportedException();

    public void ClearLatestCaches()
    {
    }

    public void ClearCaches()
    {
    }

    public void Dispose()
    {
    }

    private static SchemaRegistryException NotFound(string message) => new(message, HttpStatusCode.NotFound, 40403);

    private List<RegisteredSchema> Versions(string subject)
    {
        if (!_bySubject.TryGetValue(subject, out var versions))
        {
            versions = [];
            _bySubject[subject] = versions;
        }

        return versions;
    }

    // As a registry compares schemas: by what they mean, not their text, so key order and a repeated namespace
    // don't matter (Apache.Avro and AvroSharp write the same schema differently). A schema with references, which
    // doesn't parse alone, is compared as text.
    private static bool SameSchema(string registered, string candidate)
    {
        if (string.Equals(registered, candidate, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return string.Equals(AvroSchema.Parse(registered).ToJson(), AvroSchema.Parse(candidate).ToJson(), StringComparison.Ordinal);
        }
        catch (AvroSchemaException)
        {
            return false;
        }
    }

    private RegisteredSchema ById(int id) => _byId.Find(s => s.Id == id) ?? throw NotFound($"No schema has the ID {id}.");
}
