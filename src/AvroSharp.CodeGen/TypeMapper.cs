using System.Collections.Generic;
using System.Linq;
using AvroSharp.Generic;
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

    /// <summary>Gets how record fields become property names: the option, or the mode's default (avrogen's names for Apache).</summary>
    public PropertyNaming Naming => options.PropertyNaming ?? (options.ApacheCompatible ? PropertyNaming.Avro : PropertyNaming.PascalCase);

    /// <summary>
    /// Gets whether the generated code may use C# 11 for .NET 8 and later (behind <c>#if NET8_0_OR_GREATER</c>): UTF-8
    /// literals and static abstract interface members.
    /// </summary>
    public bool Modern => options.LanguageVersion >= 11;

    /// <summary>Gets the major C# version the generated code may use.</summary>
    public int LanguageVersion => options.LanguageVersion;

    /// <summary>
    /// Gets the struct codec type for values of <paramref name="schema"/>, used by the collection and union helpers,
    /// or <see langword="null"/> when there is none (logical types, enums, fixed, collections and unions stay inline).
    /// </summary>
    public string? Codec(AvroSchema schema) => schema switch
    {
        _ when Logical(schema) is not null => null,
        RecordSchema record => names.TypeName(record) + ".AvroCodec",
        PrimitiveSchema => schema.Type switch
        {
            AvroSchemaType.Boolean => Support + "AvroBooleanCodec",
            AvroSchemaType.Int => Support + "AvroIntCodec",
            AvroSchemaType.Long => Support + "AvroLongCodec",
            AvroSchemaType.Float => Support + "AvroFloatCodec",
            AvroSchemaType.Double => Support + "AvroDoubleCodec",
            AvroSchemaType.String => Support + "AvroStringCodec",
            AvroSchemaType.Bytes => Support + "AvroBytesCodec",
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// For a union of <c>null</c> and one type that a helper reads and writes, the helper's name suffix
    /// (<c>Int</c> for <c>ReadNullableInt</c>, or empty for the generic record helper) and the value's branch index;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public (string Suffix, int ValueIndex)? NullableHelper(UnionSchema union)
    {
        var (nullIndex, others) = Classify(union);
        if (nullIndex < 0 || others.Count != 1 || union.Branches.Count != 2)
        {
            return null;
        }

        var branch = union.Branches[others[0]];
        if (Logical(branch) is not null)
        {
            return null;
        }

        string? suffix = branch switch
        {
            RecordSchema => string.Empty,
            PrimitiveSchema => branch.Type switch
            {
                AvroSchemaType.Boolean => "Boolean",
                AvroSchemaType.Int => "Int",
                AvroSchemaType.Long => "Long",
                AvroSchemaType.Float => "Float",
                AvroSchemaType.Double => "Double",
                AvroSchemaType.String => "String",
                AvroSchemaType.Bytes => "Bytes",
                _ => null,
            },
            _ => null,
        };
        return suffix is null ? null : (suffix, others[0]);
    }

    private const string Support = "global::AvroSharp.Serialization.";

    /// <summary>
    /// Gets a field's schema default as a C# expression for the constructor, or <see langword="null"/> when there is
    /// none or it cannot be written as one (logical types, records, fixed values, and unions whose default is null).
    /// A union's default is for its first branch, as the specification says.
    /// </summary>
    public string? DefaultValue(RecordField field)
    {
        if (field.DefaultValue is not { } json)
        {
            return null;
        }

        var schema = field.Schema is UnionSchema union ? union.Branches[0] : field.Schema;
        return Literal(schema, json);
    }

    /// <summary>
    /// Gets the Avro encoding of a field's default when it has no C# literal (<see cref="DefaultValue"/> is
    /// <see langword="null"/>): records, fixed values, logical types, and collections and unions of them. The
    /// constructor decodes it with the field's reader, so it gets the value a reader gives the field when the data
    /// lacks it (#131). <see langword="null"/> when there is no default, or it is <c>null</c>.
    /// </summary>
    public byte[]? DefaultBytes(RecordField field)
    {
        if (field.DefaultValue is not { } json || DefaultValue(field) is not null)
        {
            return null;
        }

        var first = field.Schema is UnionSchema union ? union.Branches[0] : field.Schema;
        if (first.Type == AvroSchemaType.Null)
        {
            return null;
        }

        CheckDecimals(field, field.Schema, json);
        return GenericDatumWriter.Create(field.Schema).WriteToArray(GenericDatumJsonReader.ReadDefault(field.Schema, json));
    }

    // A decimal in a default that System.Decimal or the precision cannot hold is valid in the schema, but the
    // constructor, or writing the value, would throw (#141): it is an error here instead.
    private void CheckDecimals(RecordField field, AvroSchema schema, System.Text.Json.JsonElement json)
    {
        if (Logical(schema) is { Type: "decimal" } && schema.LogicalType is DecimalLogicalType dec)
        {
            var data = GenericDatumWriter.Create(schema).WriteToArray(GenericDatumJsonReader.ReadDefault(schema, json));
            try
            {
                var reader = new AvroSharp.IO.AvroReader(data);
                var scratch = new byte[data.Length + 16];
                var writer = new AvroSharp.IO.AvroWriter(scratch);
                if (schema is FixedSchema fixedSchema)
                {
                    AvroSharp.Serialization.AvroLogicalValues.WriteDecimalFixed(ref writer, AvroSharp.Serialization.AvroLogicalValues.ReadDecimalFixed(ref reader, dec.Scale, fixedSchema.Size), dec.Scale, dec.Precision, fixedSchema.Size);
                }
                else
                {
                    AvroSharp.Serialization.AvroLogicalValues.WriteDecimalBytes(ref writer, AvroSharp.Serialization.AvroLogicalValues.ReadDecimalBytes(ref reader, dec.Scale), dec.Scale, dec.Precision);
                }
            }
            catch (AvroException ex)
            {
                throw new System.InvalidOperationException($"The default of field '{field.Name}' has a decimal that the generated C# decimal cannot hold: {ex.Message}", ex);
            }

            return;
        }

        switch (schema)
        {
            case UnionSchema union:
                CheckDecimals(field, union.Branches[0], json);
                break;
            case ArraySchema array when json.ValueKind == System.Text.Json.JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                {
                    CheckDecimals(field, array.Items, item);
                }

                break;
            case MapSchema map when json.ValueKind == System.Text.Json.JsonValueKind.Object:
                foreach (var entry in json.EnumerateObject())
                {
                    CheckDecimals(field, map.Values, entry.Value);
                }

                break;
            case RecordSchema record when json.ValueKind == System.Text.Json.JsonValueKind.Object:
                foreach (var inner in record.Fields)
                {
                    if (json.TryGetProperty(inner.Name, out var value))
                    {
                        CheckDecimals(field, inner.Schema, value);
                    }
                    else if (inner.DefaultValue is { } innerDefault)
                    {
                        CheckDecimals(field, inner.Schema, innerDefault);
                    }
                }

                break;
        }
    }

    private string? Literal(AvroSchema schema, System.Text.Json.JsonElement json)
    {
        if (Logical(schema) is not null)
        {
            return null;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (schema)
        {
            case EnumSchema enumSchema when json.ValueKind == System.Text.Json.JsonValueKind.String:
                return names.TypeName(enumSchema) + "." + CSharpNames.Identifier(json.GetString()!);
            case ArraySchema array when json.ValueKind == System.Text.Json.JsonValueKind.Array:
                {
                    var items = json.EnumerateArray().Select(item => Literal(array.Items, item)).ToList();
                    return items.Any(item => item is null)
                        ? null
                        : items.Count == 0 ? $"new {TypeOf(schema)}()" : $"new {TypeOf(schema)} {{ {string.Join(", ", items)} }}";
                }

            case MapSchema map when json.ValueKind == System.Text.Json.JsonValueKind.Object:
                {
                    var entries = json.EnumerateObject().Select(entry => (entry.Name, Value: Literal(map.Values, entry.Value))).ToList();
                    return entries.Any(entry => entry.Value is null)
                        ? null
                        : entries.Count == 0
                            ? $"new {TypeOf(schema)}()"
                            : $"new {TypeOf(schema)} {{ {string.Join(", ", entries.Select(entry => $"[{CSharpNames.Literal(entry.Name)}] = {entry.Value}"))} }}";
                }

            case PrimitiveSchema:
                return schema.Type switch
                {
                    AvroSchemaType.Boolean when json.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => json.GetBoolean() ? "true" : "false",
                    AvroSchemaType.Int when json.TryGetInt32(out var i) => i == int.MinValue ? "int.MinValue" : i.ToString(invariant),
                    AvroSchemaType.Long when json.TryGetInt64(out var l) => l == long.MinValue ? "long.MinValue" : l.ToString(invariant) + "L",
                    AvroSchemaType.Float => Floating(json, "float", "f"),
                    AvroSchemaType.Double => Floating(json, "double", "d"),
                    AvroSchemaType.String when json.ValueKind == System.Text.Json.JsonValueKind.String => CSharpNames.Literal(json.GetString()!),
                    AvroSchemaType.Bytes when json.ValueKind == System.Text.Json.JsonValueKind.String => Bytes(json.GetString()!),
                    _ => null,
                };
            default:
                return null;
        }
    }

    // A floating-point default: a JSON number, or NaN/Infinity/-Infinity as a string (as Java writes them).
    private static string? Floating(System.Text.Json.JsonElement json, string type, string suffix)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (json.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return json.GetString() switch
            {
                "NaN" => type + ".NaN",
                "Infinity" => type + ".PositiveInfinity",
                "-Infinity" => type + ".NegativeInfinity",
                _ => null,
            };
        }

        if (json.ValueKind != System.Text.Json.JsonValueKind.Number || !json.TryGetDouble(out var value))
        {
            return null;
        }

        // A number beyond the type's range reads as an infinity, and has no literal of its own (#131).
        var isFloat = string.Equals(suffix, "f", System.StringComparison.Ordinal);
        var rounded = isFloat ? (float)value : value;
        if (double.IsInfinity(rounded))
        {
            return type + (rounded > 0 ? ".PositiveInfinity" : ".NegativeInfinity");
        }

        var text = isFloat ? ((float)value).ToString("R", invariant) : value.ToString("R", invariant);
        return text.Contains('E', System.StringComparison.Ordinal) || text.Contains('.', System.StringComparison.Ordinal) ? text + suffix : text + ".0" + suffix;
    }

    // A bytes default is a JSON string whose characters are the bytes (code points 0 to 255).
    private static string? Bytes(string text)
    {
        if (text.Any(c => c > 0xFF))
        {
            return null;
        }

        return text.Length == 0
            ? "global::System.Array.Empty<byte>()"
            : "new byte[] { " + string.Join(", ", text.Select(c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture))) + " }";
    }

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
        foreach (var branch in others.Select(index => union.Branches[index]))
        {
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
