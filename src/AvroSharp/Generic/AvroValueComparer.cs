using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Compares <see cref="AvroValue"/> data by the specification's sort order for one schema, as Apache Avro Java's
/// <c>GenericData.compare</c> does. Instances are cached per schema and are thread-safe.
/// </summary>
/// <remarks>
/// <para>The order, from the specification:</para>
/// <list type="bullet">
/// <item><description><c>null</c> values are equal; <c>false</c> is less than <c>true</c>; numbers compare by value.</description></item>
/// <item><description>
/// <c>float</c> and <c>double</c> compare as Java's <c>Float.compare</c> and <c>Double.compare</c> do: NaN is the
/// largest value and equals itself, and -0 is less than 0.
/// </description></item>
/// <item><description>Strings compare by their UTF-8 bytes, which is code point order; <c>bytes</c> and <c>fixed</c> byte by byte, unsigned.</description></item>
/// <item><description>Enums compare by the position of their symbol in the schema.</description></item>
/// <item><description>Arrays compare item by item; an array that is the start of a longer one is less.</description></item>
/// <item><description>Unions compare by branch first, then by value.</description></item>
/// <item><description>
/// Records compare field by field, in schema order, by each field's <see cref="RecordField.Order"/>:
/// <see cref="FieldOrder.Descending"/> reverses it, and <see cref="FieldOrder.Ignore"/> leaves the field out.
/// </description></item>
/// <item><description>Maps have no order: comparing two maps throws, so a map field needs <see cref="FieldOrder.Ignore"/>.</description></item>
/// </list>
/// <para>
/// Logical types compare by their underlying type, as in Java: a <c>decimal</c> by its bytes, so negative decimals
/// sort after positive ones.
/// </para>
/// <para>
/// This order is not <see cref="AvroValue.Equals(AvroValue)"/>: values that differ in an ignored field compare as 0,
/// NaNs with different bit patterns compare as 0, and an <c>int</c> and a <c>long</c> of the same number compare as 0
/// under a <c>long</c> schema, while <see cref="AvroValue.Equals(AvroValue)"/> tells all of these apart.
/// </para>
/// </remarks>
/// <seealso cref="AvroBinaryComparer"/>
public sealed class AvroValueComparer : IComparer<AvroValue>
{
    private static readonly ConditionalWeakTable<AvroSchema, AvroValueComparer> s_cache = new();

    private readonly Node _root;

    private AvroValueComparer(AvroSchema schema)
    {
        Schema = schema;
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema values are compared by.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the comparer for <paramref name="schema"/>, compiling it on first use.</summary>
    /// <param name="schema">The schema of the values.</param>
    public static AvroValueComparer Create(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return s_cache.GetValue(schema, static s => new AvroValueComparer(s));
    }

    /// <summary>Compares two values of <see cref="Schema"/>.</summary>
    /// <param name="x">The first value.</param>
    /// <param name="y">The second value.</param>
    /// <returns>-1, 0 or 1, as <paramref name="x"/> sorts before, with or after <paramref name="y"/>.</returns>
    /// <exception cref="AvroException">
    /// A value does not match the schema, two maps are compared, or records are nested more than
    /// <see cref="GenericDatumWriterOptions.DefaultMaxDepth"/> levels deep.
    /// </exception>
    public int Compare(AvroValue x, AvroValue y) => _root.Compare(x, y, 0);

    private static AvroException Mismatch(AvroSchema schema, in AvroValue value) =>
        new($"A {value.Kind} value cannot be compared as {schema.CanonicalForm}.");

    private abstract class Node
    {
        // depth: the number of records around the values.
        public abstract int Compare(in AvroValue x, in AvroValue y, int depth);
    }

    private sealed class Builder
    {
        // Schemas compare by reference.
        private readonly Dictionary<RecordSchema, RecordNode> _records = [];

        public Node Build(AvroSchema schema) => schema switch
        {
            RecordSchema record => BuildRecord(record),
            EnumSchema enumSchema => new EnumNode(enumSchema),
            FixedSchema fixedSchema => new FixedNode(fixedSchema),
            ArraySchema array => new ArrayNode(Build(array.Items)),
            MapSchema => new MapNode(),
            UnionSchema union => new UnionNode(union, union.Branches.ConvertAll(Build)),
            _ => new PrimitiveNode(schema),
        };

        private RecordNode BuildRecord(RecordSchema record)
        {
            // Registered before the fields are built, so recursive records refer to the same node.
            if (_records.TryGetValue(record, out var existing))
            {
                return existing;
            }

            var node = new RecordNode(record);
            _records.Add(record, node);
            node.Fields = record.Fields.ConvertAll(f => f.Order == FieldOrder.Ignore ? null : Build(f.Schema));
            return node;
        }
    }

    private sealed class PrimitiveNode(AvroSchema schema) : Node
    {
        private readonly AvroSchemaType _type = schema.Type;

        public override int Compare(in AvroValue x, in AvroValue y, int depth) => _type switch
        {
            AvroSchemaType.Null => Null(x) | Null(y),
            AvroSchemaType.Boolean => Boolean(x).CompareTo(Boolean(y)),
            AvroSchemaType.Int => Math.Sign(Int(x).CompareTo(Int(y))),
            AvroSchemaType.Long => Math.Sign(Long(x).CompareTo(Long(y))),
            AvroSchemaType.Float => SortOrder.CompareSingle(Single(x), Single(y)),
            AvroSchemaType.Double => SortOrder.CompareDouble(Double(x), Double(y)),
            AvroSchemaType.Bytes => SortOrder.CompareBytes(Bytes(x), Bytes(y)),
            _ => SortOrder.CompareUtf8(String(x), String(y)),
        };

        private int Null(in AvroValue value) => value.IsNull ? 0 : throw Mismatch(schema, value);

        private bool Boolean(in AvroValue value) => value.IsBoolean ? value.Bits != 0 : throw Mismatch(schema, value);

        private int Int(in AvroValue value) => value.IsInt ? (int)value.Bits : throw Mismatch(schema, value);

        private long Long(in AvroValue value) => value.IsLong || value.IsInt ? value.Bits : throw Mismatch(schema, value);

        // The values the writers take for these types: ints and longs widen.
        private float Single(in AvroValue value) =>
            value.IsFloat ? value.SingleUnchecked
            : value.IsInt || value.IsLong ? value.Bits
            : throw Mismatch(schema, value);

        private double Double(in AvroValue value) =>
            value.IsDouble ? value.DoubleUnchecked
            : value.IsFloat ? value.SingleUnchecked
            : value.IsInt || value.IsLong ? value.Bits
            : throw Mismatch(schema, value);

        private byte[] Bytes(in AvroValue value) => value.Reference as byte[] ?? throw Mismatch(schema, value);

        private string String(in AvroValue value) => value.Reference as string ?? throw Mismatch(schema, value);
    }

    private sealed class RecordNode(RecordSchema schema) : Node
    {
        private readonly FieldOrder[] _orders = schema.Fields.ConvertAll(f => f.Order);

        // null for an ignored field.
        public Node?[] Fields { get; set; } = [];

        public override int Compare(in AvroValue x, in AvroValue y, int depth)
        {
            var left = Record(x);
            var right = Record(y);
            if (depth >= GenericDatumWriterOptions.DefaultMaxDepth)
            {
                throw new AvroException($"Records are nested more than {GenericDatumWriterOptions.DefaultMaxDepth} levels deep; a record may contain itself.");
            }

            var fields = Fields;
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i] is not { } field)
                {
                    continue;
                }

                var result = field.Compare(left.ValueAt(i), right.ValueAt(i), depth + 1);
                if (result != 0)
                {
                    return _orders[i] == FieldOrder.Descending ? -result : result;
                }
            }

            return 0;
        }

        private GenericRecord Record(in AvroValue value) =>
            value.Reference is GenericRecord record
            && (ReferenceEquals(record.Schema, schema) || record.Schema.Name == schema.Name)
            && record.FieldCount == Fields.Length
                ? record
                : throw Mismatch(schema, value);
    }

    private sealed class EnumNode(EnumSchema schema) : Node
    {
        private readonly EnumSymbolMap _symbols = new(schema);

        // Each value's own symbol, found in this schema, as the writers find it.
        public override int Compare(in AvroValue x, in AvroValue y, int depth) =>
            Math.Sign(_symbols.Ordinal(x).CompareTo(_symbols.Ordinal(y)));
    }

    private sealed class FixedNode(FixedSchema schema) : Node
    {
        public override int Compare(in AvroValue x, in AvroValue y, int depth) =>
            SortOrder.CompareBytes(GenericDatumWriter.FixedBytes(schema, x), GenericDatumWriter.FixedBytes(schema, y));
    }

    private sealed class ArrayNode(Node items) : Node
    {
        public override int Compare(in AvroValue x, in AvroValue y, int depth)
        {
            var left = List(x);
            var right = List(y);
            var count = Math.Min(left.Count, right.Count);
            for (var i = 0; i < count; i++)
            {
                var result = items.Compare(left[i], right[i], depth);
                if (result != 0)
                {
                    return result;
                }
            }

            return Math.Sign(left.Count.CompareTo(right.Count));
        }

        private static IReadOnlyList<AvroValue> List(in AvroValue value) =>
            value.Kind == AvroValueKind.Array
                ? (IReadOnlyList<AvroValue>)value.Reference!
                : throw new AvroException($"A {value.Kind} value cannot be compared as an array.");
    }

    private sealed class MapNode : Node
    {
        public override int Compare(in AvroValue x, in AvroValue y, int depth) => throw new AvroException(SortOrder.MapsMessage);
    }

    private sealed class UnionNode(UnionSchema schema, Node[] branches) : Node
    {
        private readonly UnionBranchSelector _selector = new(schema);

        public override int Compare(in AvroValue x, in AvroValue y, int depth)
        {
            var left = Index(x);
            var right = Index(y);
            return left == right ? branches[left].Compare(x, y, depth) : Math.Sign(left.CompareTo(right));
        }

        // The branch the writers would write the value as.
        private int Index(in AvroValue value) => _selector.IndexOf(value) is var index and >= 0 ? index : throw Mismatch(schema, value);
    }
}
