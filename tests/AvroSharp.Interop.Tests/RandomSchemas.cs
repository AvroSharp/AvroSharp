using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CsCheck;

namespace AvroSharp.Interop.Tests;

/// <summary>Random, valid Avro schema JSON for property-based tests.</summary>
internal static class RandomSchemas
{
    private static readonly string?[] s_namespaces = [null, null, "", "a", "a.b", "com.example.events"];

    private static readonly (string Type, string? Logical)[] s_primitives =
    [
        ("null", null), ("boolean", null), ("int", null), ("long", null), ("float", null), ("double", null),
        ("bytes", null), ("string", null),
        ("int", "date"), ("int", "time-millis"), ("long", "time-micros"), ("long", "timestamp-millis"),
        ("long", "timestamp-micros"), ("long", "timestamp-nanos"), ("long", "local-timestamp-millis"),
        ("long", "local-timestamp-micros"), ("long", "local-timestamp-nanos"), ("string", "uuid"),
        ("bytes", "big-decimal"), ("bytes", "decimal"), ("string", "unknown-logical-type"),
    ];

    /// <summary>Generates schema JSON with nested named types, namespaces, references and unions.</summary>
    public static Gen<string> Json { get; } = ShapeGen(4).Select(shape => new Emitter(apacheCompatible: false).Emit(shape));

    /// <summary>
    /// Like <see cref="Json"/>, but without the two constructs Apache.Avro (C#) handles differently from the
    /// specification: logical types on <c>fixed</c>, and an explicit empty namespace. See <c>ApacheAvroKnownDeviationTests</c>.
    /// </summary>
    public static Gen<string> ApacheCompatibleJson { get; } = ShapeGen(4).Select(shape => new Emitter(apacheCompatible: true).Emit(shape));

    /// <summary>
    /// Like <see cref="ApacheCompatibleJson"/>, without logical types. Logical types do not change the binary encoding,
    /// and Apache.Avro's generic reader converts them to .NET types, which would get in the way of data round trips.
    /// </summary>
    public static Gen<string> ApacheCompatibleJsonWithoutLogicalTypes { get; } = ShapeGen(4).Select(shape => new Emitter(apacheCompatible: true, logicalTypes: false).Emit(shape));

    private static Gen<Shape> ShapeGen(int depth)
    {
        var primitive = Gen.Int[0, s_primitives.Length - 1].Select(i => (Shape)new PrimitiveShape(i));
        var enumeration = Gen.Select(Gen.Int[1, 5], Gen.Int[0, s_namespaces.Length - 1], (n, ns) => (Shape)new EnumShape(n, ns));
        var fixedType = Gen.Select(Gen.Int[1, 20], Gen.Int[0, s_namespaces.Length - 1], Gen.Int[0, 3], (size, ns, kind) => (Shape)new FixedShape(size, ns, kind));
        var reference = Gen.Int[0, 100].Select(i => (Shape)new ReferenceShape(i));
        var leaf = Gen.Frequency((6, primitive), (2, enumeration), (2, fixedType), (2, reference));
        if (depth == 0)
        {
            return leaf;
        }

        var child = ShapeGen(depth - 1);
        var record = Gen.Select(child.Array[0, 5], Gen.Int[0, s_namespaces.Length - 1], (fields, ns) => (Shape)new RecordShape(fields, ns));
        var array = child.Select(items => (Shape)new ArrayShape(items));
        var map = child.Select(values => (Shape)new MapShape(values));
        var union = child.Array[0, 5].Select(branches => (Shape)new UnionShape(branches));
        return Gen.Frequency((4, leaf), (3, record), (1, array), (1, map), (2, union));
    }

    private abstract record Shape;

    private sealed record PrimitiveShape(int Index) : Shape;

    private sealed record EnumShape(int Symbols, int Namespace) : Shape;

    private sealed record FixedShape(int Size, int Namespace, int Kind) : Shape;

    private sealed record ReferenceShape(int Which) : Shape;

    private sealed record RecordShape(Shape[] Fields, int Namespace) : Shape;

    private sealed record ArrayShape(Shape Items) : Shape;

    private sealed record MapShape(Shape Values) : Shape;

    private sealed record UnionShape(Shape[] Branches) : Shape;

    /// <summary>Turns a shape into JSON, keeping names unique and unions valid.</summary>
    private sealed class Emitter(bool apacheCompatible, bool logicalTypes = true)
    {
        private readonly StringBuilder _json = new();
        private readonly List<(string FullName, bool InNullNamespace)> _defined = [];
        private int _counter;

        public string Emit(Shape shape)
        {
            // A top-level union may not hold another union, but any other shape is fine.
            Write(shape, enclosingNamespace: null);
            return _json.ToString();
        }

        private void Write(Shape shape, string? enclosingNamespace)
        {
            switch (shape)
            {
                case PrimitiveShape p:
                    WritePrimitive(logicalTypes ? s_primitives[p.Index] : (s_primitives[p.Index].Type, null));
                    break;
                case ReferenceShape r:
                    _json.Append('"').Append(Resolve(r, enclosingNamespace) ?? "int").Append('"');
                    break;
                case EnumShape e:
                    WriteEnum(e, enclosingNamespace);
                    break;
                case FixedShape f:
                    WriteFixed(f, enclosingNamespace);
                    break;
                case RecordShape rec:
                    WriteRecord(rec, enclosingNamespace);
                    break;
                case ArrayShape a:
                    _json.Append("{\"type\":\"array\",\"items\":");
                    Write(a.Items, enclosingNamespace);
                    _json.Append('}');
                    break;
                case MapShape m:
                    _json.Append("{\"type\":\"map\",\"values\":");
                    Write(m.Values, enclosingNamespace);
                    _json.Append('}');
                    break;
                case UnionShape u:
                    WriteUnion(u, enclosingNamespace);
                    break;
                default:
                    throw new InvalidOperationException(shape.ToString());
            }
        }

        private void WriteEnum(EnumShape e, string? enclosingNamespace)
        {
            var (name, ns) = NewName("E", e.Namespace, enclosingNamespace);
            _json.Append("{\"type\":\"enum\",\"name\":\"").Append(name).Append('"');
            AppendNamespace(ns);
            _json.Append(",\"symbols\":[").Append(string.Join(",", Enumerable.Range(0, e.Symbols).Select(i => $"\"S{i}\""))).Append("]}");
        }

        private void WriteFixed(FixedShape f, string? enclosingNamespace)
        {
            var (name, ns) = NewName("F", f.Namespace, enclosingNamespace);
            _json.Append("{\"type\":\"fixed\",\"name\":\"").Append(name).Append('"');
            AppendNamespace(ns);
            _json.Append(",\"size\":").Append(f.Size.ToString(CultureInfo.InvariantCulture));
            if (!apacheCompatible && f.Kind == 1 && f.Size > 0)
            {
                var precision = Math.Max(1, Math.Min(38, (int)Math.Floor(Math.Log10(2) * ((8.0 * f.Size) - 1))));
                _json.Append(",\"logicalType\":\"decimal\",\"precision\":").Append(precision.ToString(CultureInfo.InvariantCulture)).Append(",\"scale\":1");
            }

            _json.Append('}');
        }

        private void WriteRecord(RecordShape rec, string? enclosingNamespace)
        {
            var (name, ns) = NewName("R", rec.Namespace, enclosingNamespace);
            var effective = ns ?? enclosingNamespace;
            _json.Append("{\"type\":\"record\",\"name\":\"").Append(name).Append('"');
            AppendNamespace(ns);
            _json.Append(",\"fields\":[");
            for (var i = 0; i < rec.Fields.Length; i++)
            {
                _json.Append(i > 0 ? "," : string.Empty).Append("{\"name\":\"f").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\",\"type\":");
                Write(rec.Fields[i], string.IsNullOrEmpty(effective) ? null : effective);
                _json.Append('}');
            }

            _json.Append("]}");
        }

        private void WriteUnion(UnionShape union, string? enclosingNamespace)
        {
            // Keep at most one branch per unnamed type and never nest unions, as the specification requires.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _json.Append('[');
            var first = true;
            foreach (var branch in union.Branches)
            {
                var key = branch switch
                {
                    PrimitiveShape p => s_primitives[p.Index].Type,
                    ArrayShape => "array",
                    MapShape => "map",
                    UnionShape => null,
                    ReferenceShape r => Resolve(r, enclosingNamespace) ?? "int",
                    _ => Guid.NewGuid().ToString(),
                };

                if (key is null || !seen.Add(key))
                {
                    continue;
                }

                _json.Append(first ? string.Empty : ",");
                first = false;
                var definedBefore = _defined.Count;
                Write(branch, enclosingNamespace);

                // A new named branch may be referenced by a later branch, which would duplicate it.
                if (branch is EnumShape or FixedShape or RecordShape)
                {
                    seen.Add(_defined[definedBefore].FullName);
                }
            }

            _json.Append(']');
        }

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

        private (string Name, string? Namespace) NewName(string prefix, int namespaceChoice, string? enclosingNamespace)
        {
            var name = prefix + _counter.ToString(CultureInfo.InvariantCulture);
            _counter++;
            var ns = s_namespaces[namespaceChoice];
            if (apacheCompatible && ns is { Length: 0 })
            {
                ns = null;
            }

            var effective = ns ?? enclosingNamespace;
            _defined.Add((string.IsNullOrEmpty(effective) ? name : effective + "." + name, string.IsNullOrEmpty(effective)));
            return (name, ns);
        }

        /// <summary>
        /// Picks a previously defined type. Apache.Avro (C#) cannot resolve a null-namespace type from inside a
        /// namespace (the specification's reference implementation, Java, falls back to the null namespace), so
        /// in Apache-compatible mode such references are avoided.
        /// </summary>
        private string? Resolve(ReferenceShape reference, string? enclosingNamespace)
        {
            var candidates = apacheCompatible && enclosingNamespace is not null
                ? _defined.Where(d => !d.InNullNamespace).ToList()
                : _defined;
            return candidates.Count == 0 ? null : candidates[reference.Which % candidates.Count].FullName;
        }

        private void AppendNamespace(string? ns)
        {
            if (ns is not null)
            {
                _json.Append(",\"namespace\":\"").Append(ns).Append('"');
            }
        }
    }
}
