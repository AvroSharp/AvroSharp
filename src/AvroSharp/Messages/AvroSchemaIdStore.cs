using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Messages;

/// <summary>
/// A thread-safe, in-memory <see cref="IAvroSchemaIdResolver"/>, for tests and for applications that know their
/// schemas and IDs ahead of time. Its asynchronous fill only looks in memory.
/// </summary>
/// <seealso cref="AvroRegistryMessageReader"/>
/// <seealso cref="AvroRegistryMessage"/>
public sealed class AvroSchemaIdStore : IAvroSchemaIdResolver
{
    private readonly ConcurrentDictionary<AvroSchemaId, AvroSchema> _schemas = new();

    /// <summary>Adds a schema under an ID; a schema already under that ID is replaced.</summary>
    /// <param name="id">The schema ID.</param>
    /// <param name="schema">The schema.</param>
    public void Add(AvroSchemaId id, AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas[id] = schema;
    }

    /// <inheritdoc/>
    public AvroSchema? GetSchema(AvroSchemaId id) => _schemas.TryGetValue(id, out var schema) ? schema : null;

    /// <inheritdoc/>
    public ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(GetSchema(id));
    }
}
