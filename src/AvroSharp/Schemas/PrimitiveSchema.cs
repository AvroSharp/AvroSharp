using System.Collections.Generic;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>
/// A primitive schema: <c>null</c>, <c>boolean</c>, <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c>,
/// <c>bytes</c> or <c>string</c>, optionally annotated with a <see cref="AvroLogicalType">logical type</see>.
/// </summary>
public sealed class PrimitiveSchema : AvroSchema
{
    /// <summary>Initializes a primitive schema.</summary>
    /// <param name="type">A primitive schema type.</param>
    /// <param name="logicalType">An optional logical type; it must be valid for <paramref name="type"/>.</param>
    /// <param name="properties">Optional custom properties.</param>
    /// <exception cref="AvroSchemaException"><paramref name="type"/> is not primitive, or the logical type does not apply to it.</exception>
    public PrimitiveSchema(AvroSchemaType type, AvroLogicalType? logicalType = null, IReadOnlyDictionary<string, JsonElement>? properties = null)
        : base(type, logicalType, properties)
    {
        if (type > AvroSchemaType.String)
        {
            throw new AvroSchemaException($"'{AvroNames.GetTypeName(type)}' is not a primitive type.");
        }

        if (logicalType is not null && !logicalType.IsValidFor(this))
        {
            throw new AvroSchemaException($"The '{logicalType.Name}' logical type cannot annotate '{AvroNames.GetTypeName(type)}'.");
        }
    }

    /// <summary>Gets the type name, for example <c>int</c>.</summary>
    public string TypeName => AvroNames.GetTypeName(Type);

    /// <summary>
    /// Gets the shared instance for a primitive type (no logical type, no properties).
    /// </summary>
    /// <param name="type">A primitive schema type.</param>
    public static PrimitiveSchema Get(AvroSchemaType type) => type switch
    {
        AvroSchemaType.Null => Null,
        AvroSchemaType.Boolean => Boolean,
        AvroSchemaType.Int => Int,
        AvroSchemaType.Long => Long,
        AvroSchemaType.Float => Float,
        AvroSchemaType.Double => Double,
        AvroSchemaType.Bytes => Bytes,
        AvroSchemaType.String => String,
        _ => throw new AvroSchemaException($"'{AvroNames.GetTypeName(type)}' is not a primitive type."),
    };
}
