using System.Globalization;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>
/// Emits the statements that write and read one value, in schema order, calling <c>AvroWriter</c>/<c>AvroReader</c>
/// directly: no schema lookups, virtual calls or boxing (except for unions mapped to <c>object?</c>).
/// </summary>
internal sealed class SerializerEmitter(CSharpNames names, TypeMapper types)
{
    private const string Support = "global::AvroSharp.Serialization.AvroGeneratedCode";
    private const string CollectionsMarshal = "global::System.Runtime.InteropServices.CollectionsMarshal";

    private int _next;

    /// <summary>Writes <paramref name="expression"/>. <paramref name="field"/> (<c>Record.field</c>) names it in errors.</summary>
    public void Write(CodeWriter w, AvroSchema schema, string expression, string field)
    {
        // Logical types convert at the edge (AvroLogicalValues); every mapping is a non-nullable value type.
        if (types.Logical(schema) is { } logical)
        {
            w.Line(logical.Write(expression));
            return;
        }

        switch (schema)
        {
            case RecordSchema record:
                w.Line($"{names.TypeName(record)}.WriteCore(ref writer, {NotNull(expression, field)}, depth + 1);");
                break;
            case EnumSchema:
                w.Line($"writer.WriteEnum((int){expression});");
                break;
            case FixedSchema:
                w.Line($"writer.WriteFixed({NotNull(expression, field)}.Value);");
                break;
            case ArraySchema array:
                WriteArray(w, array, expression, field);
                break;
            case MapSchema map:
                WriteMap(w, map, expression, field);
                break;
            case UnionSchema union:
                WriteUnion(w, union, expression, field);
                break;
            default:
                WritePrimitive(w, schema.Type, expression, field);
                break;
        }
    }

    /// <summary>Reads a value and assigns it to <paramref name="target"/>.</summary>
    public void Read(CodeWriter w, AvroSchema schema, string target)
    {
        if (types.Logical(schema) is { } logical)
        {
            w.Line($"{target} = {logical.Read};");
            return;
        }

        switch (schema)
        {
            case RecordSchema record:
                w.Line($"{target} = {names.TypeName(record)}.ReadCore(ref reader, depth + 1);");
                break;
            case EnumSchema enumSchema:
                w.Line($"{target} = ({names.TypeName(enumSchema)}){Support}.ReadEnumOrdinal(ref reader, {Int(enumSchema.Symbols.Count)}, {CSharpNames.Literal(enumSchema.FullName)});");
                break;
            case FixedSchema fixedSchema:
                w.Line($"{target} = new {names.TypeName(fixedSchema)}(reader.ReadFixedSpan({Int(fixedSchema.Size)}).ToArray());");
                break;
            case ArraySchema array:
                ReadArray(w, array, target);
                break;
            case MapSchema map:
                ReadMap(w, map, target);
                break;
            case UnionSchema union:
                ReadUnion(w, union, target);
                break;
            default:
                w.Line($"{target} = {ReadPrimitive(schema.Type)};");
                break;
        }
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string NotNull(string expression, string field) =>
        $"({expression} ?? throw {Support}.NullValue({CSharpNames.Literal(field)}))";

    private static void WritePrimitive(CodeWriter w, AvroSchemaType type, string expression, string field)
    {
        switch (type)
        {
            case AvroSchemaType.Null:
                // Nothing is written for null.
                break;
            case AvroSchemaType.Boolean:
                w.Line($"writer.WriteBoolean({expression});");
                break;
            case AvroSchemaType.Int:
                w.Line($"writer.WriteInt({expression});");
                break;
            case AvroSchemaType.Long:
                w.Line($"writer.WriteLong({expression});");
                break;
            case AvroSchemaType.Float:
                w.Line($"writer.WriteFloat({expression});");
                break;
            case AvroSchemaType.Double:
                w.Line($"writer.WriteDouble({expression});");
                break;
            case AvroSchemaType.Bytes:
                w.Line($"writer.WriteBytes({NotNull(expression, field)});");
                break;
            default:
                w.Line($"writer.WriteString({NotNull(expression, field)});");
                break;
        }
    }

    private static string ReadPrimitive(AvroSchemaType type) => type switch
    {
        AvroSchemaType.Null => "null",
        AvroSchemaType.Boolean => "reader.ReadBoolean()",
        AvroSchemaType.Int => "reader.ReadInt()",
        AvroSchemaType.Long => "reader.ReadLong()",
        AvroSchemaType.Float => "reader.ReadFloat()",
        AvroSchemaType.Double => "reader.ReadDouble()",
        AvroSchemaType.Bytes => "reader.ReadBytes()",
        _ => "reader.ReadString()",
    };

    /// <summary>The bulk reader for arrays of a fixed-width or varint primitive, or <see langword="null"/> (also for mapped logical types).</summary>
    private string? BulkReader(AvroSchema items) => items.Type switch
    {
        _ when items is not PrimitiveSchema || types.Logical(items) is not null => null,
        AvroSchemaType.Int => "ReadInts",
        AvroSchemaType.Long => "ReadLongs",
        AvroSchemaType.Float => "ReadFloats",
        AvroSchemaType.Double => "ReadDoubles",
        _ => null,
    };

    private void WriteArray(CodeWriter w, ArraySchema array, string expression, string field)
    {
        var n = _next++;
        w.Open();
        w.Line($"var items{n} = {NotNull(expression, field)};");
        w.Open($"if (items{n}.Count > 0)");
        w.Line($"writer.WriteBlockCount(items{n}.Count);");
        if (array.Items is PrimitiveSchema && types.Logical(array.Items) is null && array.Items.Type is AvroSchemaType.Double or AvroSchemaType.Float)
        {
            // Fixed-width items are one copy on little-endian hardware.
            var bulk = array.Items.Type == AvroSchemaType.Double ? "WriteDoubles" : "WriteFloats";
            w.Directive("#if NET8_0_OR_GREATER");
            w.Line($"writer.{bulk}({CollectionsMarshal}.AsSpan(items{n}));");
            w.Directive("#else");
            w.Open($"foreach (var item{n} in items{n})");
            Write(w, array.Items, $"item{n}", field);
            w.Close();
            w.Directive("#endif");
        }
        else
        {
            w.Directive("#if NET8_0_OR_GREATER");
            w.Line($"foreach (var item{n} in {CollectionsMarshal}.AsSpan(items{n}))");
            w.Directive("#else");
            w.Line($"foreach (var item{n} in items{n})");
            w.Directive("#endif");
            w.Open();
            Write(w, array.Items, $"item{n}", field);
            w.Close();
        }

        w.Close();
        w.Line("writer.WriteBlockEnd();");
        w.Close();
    }

    private void WriteMap(CodeWriter w, MapSchema map, string expression, string field)
    {
        var n = _next++;
        w.Open();
        w.Line($"var map{n} = {NotNull(expression, field)};");
        w.Open($"if (map{n}.Count > 0)");
        w.Line($"writer.WriteBlockCount(map{n}.Count);");
        w.Open($"foreach (var entry{n} in map{n})");
        w.Line($"writer.WriteString(entry{n}.Key);");
        Write(w, map.Values, $"entry{n}.Value", field);
        w.Close();
        w.Close();
        w.Line("writer.WriteBlockEnd();");
        w.Close();
    }

    private void WriteUnion(CodeWriter w, UnionSchema union, string expression, string field)
    {
        var (nullIndex, others) = TypeMapper.Classify(union);
        var n = _next++;
        if (others.Count == 0)
        {
            w.Line($"writer.WriteUnionIndex({Int(nullIndex)});");
        }
        else if (others.Count == 1 && nullIndex < 0)
        {
            w.Line($"writer.WriteUnionIndex({Int(others[0])});");
            Write(w, union.Branches[others[0]], expression, field);
        }
        else if (others.Count == 1)
        {
            w.Open($"if ({expression} is {{ }} value{n})");
            w.Line($"writer.WriteUnionIndex({Int(others[0])});");
            Write(w, union.Branches[others[0]], $"value{n}", field);
            w.Close();
            w.Open("else");
            w.Line($"writer.WriteUnionIndex({Int(nullIndex)});");
            w.Close();
        }
        else
        {
            // object?: the branch is chosen from the value's runtime type.
            w.Open($"switch ({expression})");
            if (nullIndex >= 0)
            {
                w.Line("case null:");
                w.Indent();
                w.Line($"writer.WriteUnionIndex({Int(nullIndex)});");
                w.Line("break;");
                w.Outdent();
            }

            foreach (var index in others)
            {
                var branch = union.Branches[index];
                w.Line($"case {types.TypeOf(branch)} value{n}_{Int(index)}:");
                w.Indent();
                w.Line($"writer.WriteUnionIndex({Int(index)});");
                Write(w, branch, $"value{n}_{Int(index)}", field);
                w.Line("break;");
                w.Outdent();
            }

            w.Line("default:");
            w.Indent();
            w.Line($"throw {Support}.UnionValueMismatch({expression}, {CSharpNames.Literal(field)});");
            w.Outdent();
            w.Close();
        }
    }

    private void ReadArray(CodeWriter w, ArraySchema array, string target)
    {
        var n = _next++;
        var itemType = types.TypeOf(array.Items);
        w.Open();
        w.Line($"var items{n} = new {TypeMapper.ListType}<{itemType}>();");
        w.Line($"int count{n};");
        w.Open($"while ((count{n} = {Support}.ReadBlockItemCount(ref reader, {Int(TypeMapper.MinimumSize(array.Items))}, items{n}.Count)) != 0)");
        if (BulkReader(array.Items) is { } bulk)
        {
            // Runs of small varints are decoded together; fixed-width items are one copy on little-endian hardware.
            w.Directive("#if NET8_0_OR_GREATER");
            w.Line($"var start{n} = items{n}.Count;");
            w.Line($"{CollectionsMarshal}.SetCount(items{n}, start{n} + count{n});");
            w.Line($"reader.{bulk}({CollectionsMarshal}.AsSpan(items{n}).Slice(start{n}, count{n}));");
            w.Directive("#else");
            ReadItems(w, array, n, itemType);
            w.Directive("#endif");
        }
        else
        {
            ReadItems(w, array, n, itemType);
        }

        w.Close();
        w.Line($"{target} = items{n};");
        w.Close();
    }

    private void ReadItems(CodeWriter w, ArraySchema array, int n, string itemType)
    {
        w.Open($"if (items{n}.Count == 0)");
        w.Line($"items{n}.Capacity = {Support}.InitialCapacity(count{n});");
        w.Close();
        w.Open($"for (var i{n} = 0; i{n} < count{n}; i{n}++)");
        w.Line($"{itemType} item{n};");
        Read(w, array.Items, $"item{n}");
        w.Line($"items{n}.Add(item{n});");
        w.Close();
    }

    private void ReadMap(CodeWriter w, MapSchema map, string target)
    {
        var n = _next++;
        var valueType = types.TypeOf(map.Values);
        w.Open();
        w.Line($"var map{n} = new {TypeMapper.DictionaryType}<string, {valueType}>(global::System.StringComparer.Ordinal);");
        w.Line($"int count{n};");

        // Each entry has at least a key length byte.
        var minimumEntrySize = (int)System.Math.Min(1L + TypeMapper.MinimumSize(map.Values), int.MaxValue);
        w.Open($"while ((count{n} = {Support}.ReadBlockItemCount(ref reader, {Int(minimumEntrySize)}, map{n}.Count)) != 0)");
        w.Open($"for (var i{n} = 0; i{n} < count{n}; i{n}++)");
        w.Line($"var key{n} = reader.ReadString();");
        w.Line($"{valueType} value{n};");
        Read(w, map.Values, $"value{n}");
        w.Line($"map{n}[key{n}] = value{n};");
        w.Close();
        w.Close();
        w.Line($"{target} = map{n};");
        w.Close();
    }

    private void ReadUnion(CodeWriter w, UnionSchema union, string target)
    {
        var n = _next++;
        w.Line($"var index{n} = reader.ReadUnionIndex();");
        w.Open($"switch (index{n})");
        for (var i = 0; i < union.Branches.Count; i++)
        {
            w.Line($"case {Int(i)}:");
            w.Indent();
            Read(w, union.Branches[i], target);
            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Indent();
        w.Line($"throw {Support}.InvalidUnionIndex(index{n}, {Int(union.Branches.Count)});");
        w.Outdent();
        w.Close();
    }
}
