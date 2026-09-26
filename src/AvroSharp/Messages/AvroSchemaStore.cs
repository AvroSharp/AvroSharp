using System;
using System.Collections.Concurrent;
using AvroSharp.Schemas;

namespace AvroSharp.Messages;

/// <summary>A thread-safe, in-memory <see cref="IAvroSchemaStore"/>.</summary>
public sealed class AvroSchemaStore : IAvroSchemaStore
{
    private readonly ConcurrentDictionary<long, AvroSchema> _schemas = new();

    /// <summary>Creates a store holding <paramref name="schemas"/>.</summary>
    /// <param name="schemas">The schemas.</param>
    public AvroSchemaStore(params AvroSchema[] schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        foreach (var schema in schemas)
        {
            Add(schema);
        }
    }

    /// <summary>Adds a schema; a schema with the same fingerprint is replaced.</summary>
    /// <param name="schema">The schema.</param>
    public void Add(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas[schema.Fingerprint64] = schema;
    }

    /// <inheritdoc/>
    public AvroSchema? GetSchema(long fingerprint) => _schemas.TryGetValue(fingerprint, out var schema) ? schema : null;
}
