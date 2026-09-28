using System.Collections.Generic;
using System.Linq;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>Maps schemas to C# types.</summary>
/// <remarks>
/// Logical types map as <see cref="CodeGenOptions.LogicalTypes"/> says (see <see cref="LogicalTypes"/>). A union of
/// <c>null</c> and one other type maps to that type made nullable; any other union with more than one type maps to
/// <c>object?</c>.
/// </remarks>
internal sealed class TypeMapper(CSharpNames names, CodeGenOptions options)
{
    public const string ListType = "global::System.Collections.Generic.List";
    public const string DictionaryType = "global::System.Collections.Generic.Dictionary";

    /// <summary>Makes a reference type nullable: <c>T?</c> with nullable annotations (C# 8 and later), otherwise <c>T</c>.</summary>
    public string Nullable(string referenceType) => options.NullableAnnotations ? referenceType + "?" : referenceType;

    /// <summary>Gets how record fields become property names.</summary>
    public PropertyNaming Naming => options.PropertyNaming;

    /// <summary>Gets whether nullable reference type annotations are emitted (C# 8 and later).</summary>
    public bool Annotations => options.NullableAnnotations;

    /// <summary>Gets the null-forgiving operator when annotations are on, otherwise nothing.</summary>
    public string NullForgiving => options.NullableAnnotations ? "!" : string.Empty;

    /// <summary>Gets the logical-type mapping of <paramref name="schema"/>, or <see langword="null"/> when it keeps its underlying type.</summary>
    public LogicalValue? Logical(AvroSchema schema) => LogicalTypes.For(schema, options);

    /// <summary>Gets the C# type of a value of <paramref name="schema"/>.</summary>
    public string TypeOf(AvroSchema schema) => Logical(schema)?.Type ?? UnderlyingType(schema);

    /// <summary>Gets whether values of <paramref name="schema"/> are C# value types.</summary>
    public bool IsValueType(AvroSchema schema) =>
        Logical(schema) is not null
        || schema is EnumSchema
        || schema.Type is AvroSchemaType.Boolean or AvroSchemaType.Int or AvroSchemaType.Long or AvroSchemaType.Float or AvroSchemaType.Double;

    /// <summary>
    /// Gets the initializer that keeps a non-nullable property valid before it is set, or <see langword="null"/>
    /// when the type's default value is already valid (every logical-type mapping is a value type).
    /// </summary>
    public string? Initializer(AvroSchema schema) => Logical(schema) is not null ? null : schema switch
    {
        ArraySchema or MapSchema => $"new {TypeOf(schema)}()",
        RecordSchema or FixedSchema => "null" + NullForgiving,
        UnionSchema union when Classify(union) is { NullIndex: < 0, Others.Count: 1 } single => Initializer(union.Branches[single.Others[0]]),
        _ => schema.Type switch
        {
            AvroSchemaType.Bytes => "global::System.Array.Empty<byte>()",
            AvroSchemaType.String => "\"\"",
            _ => null,
        },
    };

    private string UnderlyingType(AvroSchema schema) => schema switch
    {
        NamedSchema named => names.TypeName(named),
        ArraySchema array => $"{ListType}<{TypeOf(array.Items)}>",
        MapSchema map => $"{DictionaryType}<string, {TypeOf(map.Values)}>",
        UnionSchema union => UnionType(union),
        _ => schema.Type switch
        {
            AvroSchemaType.Null => Nullable("object"),
            AvroSchemaType.Boolean => "bool",
            AvroSchemaType.Int => "int",
            AvroSchemaType.Long => "long",
            AvroSchemaType.Float => "float",
            AvroSchemaType.Double => "double",
            AvroSchemaType.Bytes => "byte[]",
            _ => "string",
        },
    };

    /// <summary>Classifies a union: its null branch (or -1) and its other branches.</summary>
    public static (int NullIndex, List<int> Others) Classify(UnionSchema union)
    {
        var nullIndex = -1;
        var others = new List<int>();
        for (var i = 0; i < union.Branches.Count; i++)
        {
            if (union.Branches[i].Type == AvroSchemaType.Null)
            {
                nullIndex = i;
            }
            else
            {
                others.Add(i);
            }
        }

        return (nullIndex, others);
    }

    /// <summary>
    /// Gets the smallest number of bytes a value can occupy, used to bound block counts before allocating. A record
    /// that is still being measured (recursion) counts as 0, so the result is a safe lower bound.
    /// </summary>
    public static int MinimumSize(AvroSchema schema) => MinimumSize(schema, []);

    private static int MinimumSize(AvroSchema schema, HashSet<RecordSchema> open) => schema switch
    {
        RecordSchema record => open.Add(record) ? MeasureRecord(record, open) : 0,
        FixedSchema fixedSchema => fixedSchema.Size,
        _ => schema.Type switch
        {
            AvroSchemaType.Null => 0,
            AvroSchemaType.Float => sizeof(float),
            AvroSchemaType.Double => sizeof(double),
            _ => 1,
        },
    };

    private static int MeasureRecord(RecordSchema record, HashSet<RecordSchema> open)
    {
        var size = record.Fields.Sum(f => (long)MinimumSize(f.Schema, open));
        open.Remove(record);
        return (int)System.Math.Min(size, int.MaxValue);
    }

    private string UnionType(UnionSchema union)
    {
        var (nullIndex, others) = Classify(union);
        if (others.Count == 1)
        {
            var branch = union.Branches[others[0]];
            var type = TypeOf(branch);
            if (nullIndex < 0)
            {
                return type;
            }

            // Nullable<T> for value types exists in every C# version; reference types need annotations (C# 8).
            return IsValueType(branch) ? type + "?" : Nullable(type);
        }

        // An object? union is written by switching on the value's runtime type, so two branches with the same C# type
        // (a string and a fixed uuid are both Guid) can be neither told apart nor compiled (CS8120).
        var seen = new Dictionary<string, AvroSchema>(System.StringComparer.Ordinal);
        foreach (var index in others)
        {
            var branch = union.Branches[index];
            var type = TypeOf(branch);
            if (seen.TryGetValue(type, out var earlier))
            {
                throw new AvroException(
                    $"The union branches {Describe(earlier)} and {Describe(branch)} both map to the C# type {type}, so generated code " +
                    "cannot tell them apart. Change the schema, or map logical types to their underlying types " +
                    "(AvroSharpLogicalTypes=raw, not available with AvroSharpApacheCompatible).");
            }

            seen.Add(type, branch);
        }

        return Nullable("object");
    }

    // A named type by its full name, anything else as its JSON (for example {"type":"string","logicalType":"uuid"}).
    private static string Describe(AvroSchema schema) => schema switch
    {
        NamedSchema named when schema.LogicalType is { } logical => $"'{named.FullName}' ({logical.Name})",
        NamedSchema named => $"'{named.FullName}'",
        _ => schema.ToString(),
    };
}
