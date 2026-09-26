using System.Collections.Generic;
using System.Linq;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>Maps schemas to C# types.</summary>
/// <remarks>
/// Logical types map to their underlying type for now (a <c>date</c> is an <c>int</c>). A union of <c>null</c> and one
/// other type maps to that type made nullable; any other union with more than one type maps to <c>object?</c>.
/// </remarks>
internal sealed class TypeMapper(CSharpNames names)
{
    public const string ListType = "global::System.Collections.Generic.List";
    public const string DictionaryType = "global::System.Collections.Generic.Dictionary";

    /// <summary>Gets the C# type of a value of <paramref name="schema"/>.</summary>
    public string TypeOf(AvroSchema schema) => schema switch
    {
        NamedSchema named => names.TypeName(named),
        ArraySchema array => $"{ListType}<{TypeOf(array.Items)}>",
        MapSchema map => $"{DictionaryType}<string, {TypeOf(map.Values)}>",
        UnionSchema union => UnionType(union),
        _ => schema.Type switch
        {
            AvroSchemaType.Null => "object?",
            AvroSchemaType.Boolean => "bool",
            AvroSchemaType.Int => "int",
            AvroSchemaType.Long => "long",
            AvroSchemaType.Float => "float",
            AvroSchemaType.Double => "double",
            AvroSchemaType.Bytes => "byte[]",
            _ => "string",
        },
    };

    /// <summary>Gets whether values of <paramref name="schema"/> are C# value types.</summary>
    public static bool IsValueType(AvroSchema schema) =>
        schema is EnumSchema
        || schema.Type is AvroSchemaType.Boolean or AvroSchemaType.Int or AvroSchemaType.Long or AvroSchemaType.Float or AvroSchemaType.Double;

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
    /// Gets the initializer that keeps a non-nullable property valid before it is set, or <see langword="null"/>
    /// when the type's default value is already valid.
    /// </summary>
    public static string? Initializer(AvroSchema schema) => schema switch
    {
        ArraySchema or MapSchema => "new()",
        RecordSchema or FixedSchema => "null!",
        UnionSchema union when Classify(union) is { NullIndex: < 0, Others.Count: 1 } single => Initializer(union.Branches[single.Others[0]]),
        _ => schema.Type switch
        {
            AvroSchemaType.Bytes => "global::System.Array.Empty<byte>()",
            AvroSchemaType.String => "\"\"",
            _ => null,
        },
    };

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
            var type = TypeOf(union.Branches[others[0]]);
            return nullIndex >= 0 ? type + "?" : type;
        }

        return "object?";
    }
}
