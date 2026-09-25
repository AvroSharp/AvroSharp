using System.Collections.Generic;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>A fixed schema: a named sequence of exactly <see cref="Size"/> bytes.</summary>
public sealed class FixedSchema : NamedSchema
{
    /// <summary>Initializes a fixed schema.</summary>
    /// <param name="name">The fixed name.</param>
    /// <param name="size">The number of bytes per value; must not be negative.</param>
    /// <param name="logicalType">An optional logical type (<c>decimal</c>, <c>uuid</c> with size 16, or <c>duration</c> with size 12).</param>
    /// <param name="doc">Optional documentation.</param>
    /// <param name="aliases">Optional alternative names.</param>
    /// <param name="properties">Optional custom properties.</param>
    /// <exception cref="AvroSchemaException">The size is negative or the logical type does not apply.</exception>
    public FixedSchema(
        SchemaName name,
        int size,
        AvroLogicalType? logicalType = null,
        string? doc = null,
        IEnumerable<SchemaName>? aliases = null,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
        : base(AvroSchemaType.Fixed, name, aliases, doc, logicalType, properties)
    {
        if (size < 0)
        {
            throw new AvroSchemaException($"The size of fixed '{name.FullName}' must not be negative, but was {size}.");
        }

        Size = size;

        if (logicalType is not null && !logicalType.IsValidFor(this))
        {
            throw new AvroSchemaException($"The '{logicalType}' logical type cannot annotate fixed '{name.FullName}' of size {size}.");
        }
    }

    /// <summary>Gets the number of bytes per value.</summary>
    public int Size { get; }
}
