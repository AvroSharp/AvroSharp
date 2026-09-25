using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>An array schema.</summary>
public sealed class ArraySchema : AvroSchema
{
    /// <summary>Initializes an array schema.</summary>
    /// <param name="items">The schema of the items.</param>
    /// <param name="properties">Optional custom properties.</param>
    public ArraySchema(AvroSchema items, IReadOnlyDictionary<string, JsonElement>? properties = null)
        : base(AvroSchemaType.Array, logicalType: null, properties)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>Gets the schema of the items.</summary>
    public AvroSchema Items { get; }
}
