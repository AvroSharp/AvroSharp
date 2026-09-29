using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CsCheck;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// Random, valid record schemas for code generation tests (#141): nested named types, references, unions and every
/// logical type, with names chosen to break generated C# and random defaults for any field type. Shapes are CsCheck
/// generators, so a failing schema shrinks to a small one.
/// </summary>
internal static class RandomCodeGenSchemas
{
    // Type names that C# or the generated members make hard: keywords, contextual keywords, names of generated
    // members, names that differ only by case, and a name also used as a namespace segment.
    private static readonly string[] s_typeNames =
    [
        "Order", "order", "Item", "Event", "Schema", "Read", "Write", "ToAvroBytes", "FromAvroBytes", "Get", "Put",
        "Size", "Value", "AsSpan", "var", "record", "file", "scoped", "required", "partial", "not", "_", "@class",
        "dynamic", "value", "events", "Equals", "ReadField", "s_schema",
    ];

    // Field names: keywords, generated member names, and names that clash after PascalCase conversion.
    private static readonly string[] s_fieldNames =
    [
        "id", "user_id", "userId", "UserId", "USER_ID", "class", "value", "Schema", "var", "_", "event", "s_default0",
        "ToString", "type", "item", "Item", "get", "HashCode", "record",
    ];

    private static readonly string[] s_namespaceSegments = ["a", "nameof", "value", "global", "record", "System", "events", "b.c"];

    private static readonly (string Type, string? Logical)[] s_primitives =
    [
        ("null", null), ("boolean", null), ("int", null), ("long", null), ("float", null), ("double", null),
        ("bytes", null), ("string", null),
        ("int", "date"), ("int", "time-millis"), ("long", "time-micros"), ("long", "timestamp-millis"),
        ("long", "timestamp-micros"), ("long", "timestamp-nanos"), ("long", "local-timestamp-millis"),
        ("long", "local-timestamp-micros"), ("long", "local-timestamp-nanos"), ("string", "uuid"),
        ("bytes", "decimal"), ("string", "unknown-logical-type"),
    ];

    /// <summary>A record schema's JSON; its named types are in namespace <paramref name="baseNamespace"/> or below it.</summary>
    public static Gen<string> Json(string baseNamespace) => Record(3).Select(shape => new Emitter(baseNamespace).Emit(shape));

    private static Gen<RecordShape> Record(int depth) =>
        Gen.Select(Gen.Int[0, s_typeNames.Length - 1], Gen.Int[0, s_namespaceSegments.Length], Field(depth).Array[1, 6],
            (name, ns, fields) => new RecordShape(name, ns, fields));

    private static Gen<FieldShape> Field(int depth) =>
        Gen.Select(Gen.Int[0, s_fieldNames.Length - 1], Type(depth), Gen.Int[0, 9], (name, type, kind) => new FieldShape(name, type, kind));

    private static Gen<Shape> Type(int depth)
    {
        var primitive = Gen.Int[0, s_primitives.Length - 1].Select(i => (Shape)new PrimitiveShape(i));
        var enumeration = Gen.Select(Gen.Int[0, s_typeNames.Length - 1], Gen.Int[0, s_namespaceSegments.Length], Gen.Int[1, 4], (name, ns, n) => (Shape)new EnumShape(name, ns, n));
        var fixedType = Gen.Select(Gen.Int[0, s_typeNames.Length - 1], Gen.Int[0, s_namespaceSegments.Length], Gen.Int[1, 16], Gen.Int[0, 3], (name, ns, size, kind) => (Shape)new FixedShape(name, ns, size, kind));
        var reference = Gen.Int[0, 100].Select(i => (Shape)new ReferenceShape(i));
        var leaf = Gen.Frequency((6, primitive), (2, enumeration), (2, fixedType), (2, reference));
        if (depth == 0)
        {
            return leaf;
        }

        var child = Type(depth - 1);
        var record = Record(depth - 1).Select(r => (Shape)r);
        var array = child.Select(items => (Shape)new ArrayShape(items));
        var map = child.Select(values => (Shape)new MapShape(values));
        var union = child.Array[1, 4].Select(branches => (Shape)new UnionShape(branches));
        return Gen.Frequency((5, leaf), (2, record), (1, array), (1, map), (2, union));
    }

    private abstract record Shape;

    private sealed record PrimitiveShape(int Index) : Shape;

    private sealed record EnumShape(int Name, int Namespace, int Symbols) : Shape;

    // Kind 0: plain; 1: decimal; 2: uuid (size 16); 3: duration (size 12).
    private sealed record FixedShape(int Name, int Namespace, int Size, int Kind) : Shape;

    private sealed record ReferenceShape(int Which) : Shape;

    // Written: the fields are as the JSON has them (a record still being written has no default yet).
    private sealed record RecordShape(int Name, int Namespace, FieldShape[] Fields, bool Written = false) : Shape;

    // Default: 0-3 none, 4-9 a default (most fields have one, so records whose every field has one are common).
    private sealed record FieldShape(int Name, Shape Type, int Default);

    private sealed record ArrayShape(Shape Items) : Shape;

    private sealed record MapShape(Shape Values) : Shape;

    private sealed record UnionShape(Shape[] Branches) : Shape;

    /// <summary>Writes a shape as schema JSON, keeping full names unique, unions valid and defaults consistent.</summary>
    private sealed class Emitter(string baseNamespace)
    {
        private readonly StringBuilder _json = new();

        // Every named type defined so far, by full name, with its shape: references pick one, and defaults follow it.
        private readonly List<(string FullName, Shape Shape)> _defined = [];
        private readonly HashSet<string> _fullNames = new(StringComparer.Ordinal);

        public string Emit(RecordShape root)
        {
            WriteRecord(root);
            return _json.ToString();
        }

        private Shape Write(Shape shape)
        {
            switch (shape)
            {
                case PrimitiveShape p:
                    WritePrimitive(s_primitives[p.Index]);
                    return shape;
                case ReferenceShape r when _defined.Count > 0:
                    var target = _defined[r.Which % _defined.Count];
                    _json.Append('"').Append(target.FullName).Append('"');
                    return target.Shape;
                case ReferenceShape:
                    _json.Append("\"int\"");
                    return new PrimitiveShape(2);
                case EnumShape e:
                    _json.Append("{\"type\":\"enum\",\"name\":\"").Append(Define(e, e.Name, e.Namespace)).Append('"')
                        .Append(",\"symbols\":[").Append(string.Join(",", Enumerable.Range(0, e.Symbols).Select(i => $"\"S{i}\""))).Append("]}");
                    return shape;
                case FixedShape f:
                    return WriteFixed(f);
                case RecordShape rec:
                    return WriteRecord(rec);
                case ArrayShape a:
                    _json.Append("{\"type\":\"array\",\"items\":");
                    var items = Write(a.Items);
                    _json.Append('}');
                    return new ArrayShape(items);
                case MapShape m:
                    _json.Append("{\"type\":\"map\",\"values\":");
                    var values = Write(m.Values);
                    _json.Append('}');
                    return new MapShape(values);
                case UnionShape u:
                    return WriteUnion(u);
                default:
                    throw new InvalidOperationException(shape.ToString());
            }
        }

        // Returns the full name, which the JSON gives as "name" (with the namespace in it).
        private string Define(Shape shape, int nameIndex, int namespaceIndex)
        {
            var ns = namespaceIndex == s_namespaceSegments.Length ? baseNamespace : baseNamespace + "." + s_namespaceSegments[namespaceIndex];
            var name = s_typeNames[nameIndex].TrimStart('@');
            var fullName = ns + "." + name;
            for (var suffix = 2; !_fullNames.Add(fullName); suffix++)
            {
                fullName = ns + "." + name + suffix.ToString(CultureInfo.InvariantCulture);
            }

            _defined.Add((fullName, shape));
            return fullName;
        }

        private FixedShape WriteFixed(FixedShape f)
        {
            var size = f.Kind switch { 2 => 16, 3 => 12, _ => f.Size };
            var sized = f with { Size = size };
            _json.Append("{\"type\":\"fixed\",\"name\":\"").Append(Define(sized, f.Name, f.Namespace)).Append('"')
                .Append(",\"size\":").Append(size.ToString(CultureInfo.InvariantCulture));
            switch (f.Kind)
            {
                case 1:
                    var precision = Math.Max(1, Math.Min(28, (int)Math.Floor(Math.Log10(2) * ((8.0 * size) - 1))));
                    _json.Append(",\"logicalType\":\"decimal\",\"precision\":").Append(precision.ToString(CultureInfo.InvariantCulture)).Append(",\"scale\":1");
                    break;
                case 2:
                    _json.Append(",\"logicalType\":\"uuid\"");
                    break;
                case 3:
                    _json.Append(",\"logicalType\":\"duration\"");
                    break;
            }

            _json.Append('}');
            return sized;
        }

        private RecordShape WriteRecord(RecordShape rec)
        {
            var fullName = Define(rec, rec.Name, rec.Namespace);
            var index = _defined.Count - 1;
            var fields = new FieldShape[rec.Fields.Length];
            _json.Append("{\"type\":\"record\",\"name\":\"").Append(fullName).Append("\",\"fields\":[");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rec.Fields.Length; i++)
            {
                var field = rec.Fields[i];
                var name = s_fieldNames[field.Name];
                for (var suffix = 2; !names.Add(name); suffix++)
                {
                    name = s_fieldNames[field.Name] + suffix.ToString(CultureInfo.InvariantCulture);
                }

                _json.Append(i > 0 ? "," : string.Empty).Append("{\"name\":\"").Append(name).Append("\",\"type\":");
                var written = Write(field.Type);
                fields[i] = field with { Type = written };
                if (field.Default >= 4 && DefaultJson(written, field.Default, 0) is { } value)
                {
                    _json.Append(",\"default\":").Append(value);
                }

                _json.Append('}');
            }

            _json.Append("]}");
            var result = rec with { Fields = fields, Written = true };
            _defined[index] = (fullName, result);
            return result;
        }

        // Writes a union with at most one branch per unnamed type or C# type, and never a union in a union; returns
        // it with the branches as written, since a default is for the first one.
        private UnionShape WriteUnion(UnionShape union)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var written = new List<Shape>();
            _json.Append('[');
            foreach (var branch in union.Branches)
            {
                var key = branch switch
                {
                    PrimitiveShape p => CSharpKind(s_primitives[p.Index]),
                    FixedShape { Kind: 1 } => "decimal",
                    FixedShape { Kind: 2 } => "guid",
                    ArrayShape => "array",
                    MapShape => "map",
                    UnionShape => null,
                    ReferenceShape => "reference" + written.Count.ToString(CultureInfo.InvariantCulture),
                    _ => Guid.NewGuid().ToString(),
                };

                // The Avro type too: a logical type counts as its underlying type in a union.
                if (key is null || !seen.Add(key) || (branch is PrimitiveShape q && !seen.Add("avro:" + s_primitives[q.Index].Type)))
                {
                    continue;
                }

                // A reference to a named type already in the union would repeat it.
                var mark = _json.Length;
                _json.Append(written.Count > 0 ? "," : string.Empty);
                var shape = Write(branch);
                if (branch is ReferenceShape && (shape is UnionShape || written.Contains(shape)))
                {
                    _json.Length = mark;
                    continue;
                }

                written.Add(shape);
            }

            if (written.Count == 0)
            {
                _json.Append("\"null\"");
                written.Add(new PrimitiveShape(0));
            }

            _json.Append(']');
            return new UnionShape([.. written]);
        }

        // The C# type a primitive maps to, so a union keeps one branch per type (two would be AVROGEN003).
        private static string CSharpKind((string Type, string? Logical) primitive) => primitive switch
        {
            (_, "date") => "date",
            (_, "time-millis" or "time-micros") => "time",
            (_, "timestamp-millis" or "timestamp-micros") => "timestamp",
            (_, "local-timestamp-millis" or "local-timestamp-micros") => "local",
            (_, "uuid") => "guid",
            (_, "decimal") => "decimal",
            ("long", "timestamp-nanos" or "local-timestamp-nanos") => "long",
            ("string", _) => "string",
            var (type, _) => type,
        };

        private void WritePrimitive((string Type, string? Logical) primitive)
        {
            switch (primitive.Logical)
            {
                case null:
                    _json.Append('"').Append(primitive.Type).Append('"');
                    break;
                case "decimal":
                    _json.Append("{\"type\":\"bytes\",\"logicalType\":\"decimal\",\"precision\":9,\"scale\":2}");
                    break;
                default:
                    _json.Append("{\"type\":\"").Append(primitive.Type).Append("\",\"logicalType\":\"").Append(primitive.Logical).Append("\"}");
                    break;
            }
        }

        /// <summary>
        /// A valid default for a written shape, as schema JSON: a union's is for its first branch, as the
        /// specification says. Null where a recursive type would make the default infinite.
        /// </summary>
        private string? DefaultJson(Shape shape, int variant, int depth)
        {
            if (depth > 4)
            {
                return null;
            }

            switch (shape)
            {
                case PrimitiveShape p:
                    return PrimitiveDefault(s_primitives[p.Index], variant);
                case EnumShape e:
                    return $"\"S{variant % e.Symbols}\"";
                // A decimal's unscaled value in range: sign bytes, then one byte (a random one could exceed the precision).
                case FixedShape { Kind: 1 } f:
                    return "\"" + string.Concat(Enumerable.Range(0, f.Size).Select(i => i == f.Size - 1 ? $"\\u00{variant * 11:x2}" : variant % 2 == 0 ? "\\u0000" : "\\u00ff")) + "\"";
                case FixedShape f:
                    return "\"" + string.Concat(Enumerable.Range(0, f.Size).Select(i => $"\\u00{(i + variant) % 256:x2}")) + "\"";
                case RecordShape { Written: true } rec:
                    return RecordDefault(rec, variant, depth);
                case ArrayShape a:
                    return variant % 2 == 0 ? "[]" : DefaultJson(a.Items, variant, depth + 1) is { } item ? $"[{item},{item}]" : null;
                case MapShape m:
                    return variant % 2 == 0 ? "{}" : DefaultJson(m.Values, variant, depth + 1) is { } value ? $"{{\"k\":{value}}}" : null;
                case UnionShape u:
                    return DefaultJson(u.Branches[0], variant, depth + 1);
                default:
                    return null;
            }
        }

        private string? RecordDefault(RecordShape rec, int variant, int depth)
        {
            // The field names as the JSON has them.
            var names = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var field in rec.Fields)
            {
                var name = s_fieldNames[field.Name];
                for (var suffix = 2; !names.Add(name); suffix++)
                {
                    name = s_fieldNames[field.Name] + suffix.ToString(CultureInfo.InvariantCulture);
                }

                if (DefaultJson(field.Type, variant, depth + 1) is not { } value)
                {
                    return null;
                }

                parts.Add($"\"{name}\":{value}");
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static string? PrimitiveDefault((string Type, string? Logical) primitive, int variant) => primitive switch
        {
            ("null", _) => "null",
            ("boolean", _) => variant % 2 == 0 ? "true" : "false",
            ("int", "time-millis") => "1000",
            // The extremes only for plain ints and longs: as dates or timestamps they are beyond .NET's range.
            ("int", null) => variant == 9 ? "-2147483648" : "7",
            ("int", _) => "7",
            ("long", null) => variant == 9 ? "-9223372036854775808" : "1000",
            ("long", _) => "1000",
            ("float", _) => variant switch { 9 => "1e39", 8 => "-1e39", 7 => "-0.0", _ => "1.5" },
            ("double", _) => variant switch { 9 => "1e400", 8 => "-4.9e-324", _ => "2.25" },
            ("string", "uuid") => "\"123e4567-e89b-12d3-a456-426614174000\"",
            ("string", _) => variant % 2 == 0 ? "\"\"" : "\"s\\\"\\u00e9\"",
            ("bytes", "decimal") => "\"\\u00ff\\u0001\"",
            ("bytes", _) => "\"\\u0000\\u00ff\"",
            _ => null,
        };
    }
}
