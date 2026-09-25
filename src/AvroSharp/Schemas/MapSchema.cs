using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>A map schema: string keys to values of one schema.</summary>
public sealed class MapSchema : AvroSchema
{
    /// <summary>Initializes a map schema.</summary>
    /// <param name="values">The schema of the values.</param>
    /// <param name="properties">Optional custom properties.</param>
    public MapSchema(AvroSchema values, IReadOnlyDictionary<string, JsonElement>? properties = null)
        : base(AvroSchemaType.Map, logicalType: null, properties)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
    }

    /// <summary>Gets the schema of the values.</summary>
    public AvroSchema Values { get; }
}
