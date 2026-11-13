using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Compares Avro binary data by the specification's sort order for one schema, without decoding it, as Apache Avro
/// Java's <c>BinaryData.compare</c> does (and as Hadoop sorts by it). Both inputs are read at once, only as far as the
/// first difference. Instances are cached per schema and are thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The order is <see cref="AvroValueComparer"/>'s: two encoded values compare as their decoded values do. Data after
/// the value is ignored.
/// </para>
/// <para>
/// The input is read with the limits of <see cref="GenericDatumReaderOptions"/>: records nested deeper than
/// <see cref="GenericDatumReaderOptions.MaxDepth"/>, or more zero-size array or map items than
/// <see cref="GenericDatumReaderOptions.MaxZeroSizeItems"/>, are rejected with <see cref="AvroDataException"/>.
/// </para>
/// </remarks>
/// <seealso cref="AvroValueComparer"/>
public sealed class AvroBinaryComparer : IComparer<byte[]?>
{
    private static readonly ConditionalWeakTable<AvroSchema, AvroBinaryComparer> s_cache = new();

    private readonly Node _root;
    private readonly int _maxDepth;
    private readonly int _maxZeroSizeItems;

    private AvroBinaryComparer(AvroSchema schema, GenericDatumReaderOptions options)
    {
        Schema = schema;
        _maxDepth = options.MaxDepth;
        _maxZeroSizeItems = options.MaxZeroSizeItems;
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema the data is written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>
    /// Gets the comparer for <paramref name="schema"/>, compiling it on first use. Comparers with the default options
    /// are cached per schema; comparers with custom options are created each time, so keep and reuse them.
    /// </summary>
    /// <param name="schema">The schema the data is written with.</param>
    /// <param name="options">Limits, or <see langword="null"/> for <see cref="GenericDatumReaderOptions.Default"/>.</param>
    public static AvroBinaryComparer Create(AvroSchema schema, GenericDatumReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return options is null || ReferenceEquals(options, GenericDatumReaderOptions.Default)
            ? s_cache.GetValue(schema, static s => new AvroBinaryComparer(s, GenericDatumReaderOptions.Default))
            : new AvroBinaryComparer(schema, options);
    }

    /// <summary>Compares two values encoded with <see cref="Schema"/>.</summary>
    /// <param name="x">The first value's encoding.</param>
    /// <param name="y">The second value's encoding.</param>
    /// <returns>-1, 0 or 1, as <paramref name="x"/> sorts before, with or after <paramref name="y"/>.</returns>
    /// <exception cref="AvroDataException">The data is malformed, or exceeds the limits.</exception>
    /// <exception cref="AvroException">Two maps are compared.</exception>
    public int Compare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        // Every compared or skipped item takes at least one byte, but for zero-size items: so the input's size, and the
        // limit on those, bound the work a block count can ask for.
        var inputs = new Inputs
        {
            X = new AvroReader(x),
            Y = new AvroReader(y),
            State = new State { ItemsLeft = (long)x.Length + y.Length + _maxZeroSizeItems, MaxDepth = _maxDepth },
        };
        return _root.Compare(ref inputs);
    }

    /// <summary>Compares two values encoded with <see cref="Schema"/>; <see langword="null"/> sorts first.</summary>
    /// <param name="x">The first value's encoding.</param>
    /// <param name="y">The second value's encoding.</param>
    /// <returns>-1, 0 or 1, as <paramref name="x"/> sorts before, with or after <paramref name="y"/>.</returns>
    int IComparer<byte[]?>.Compare(byte[]? x, byte[]? y) => (x, y) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => Compare(x, y),
    };

    // The two encodings, read side by side, and the limits they share.
    private ref struct Inputs
    {
        public AvroReader X;
        public AvroReader Y;
        public State State;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct State
    {
        public long ItemsLeft;
        public int MaxDepth;
        public int Depth;

        public void TakeItem()
        {
            if (--ItemsLeft < 0)
            {
                throw new AvroDataException("The input declares more zero-size items than allowed (GenericDatumReaderOptions.MaxZeroSizeItems).");
            }
        }
    }

    private abstract class Node
    {
        public abstract int Compare(ref Inputs inputs);

        public abstract void Skip(ref AvroReader reader, ref State state);
    }

    private sealed class Builder
    {
        // Schemas compare by reference.
        private readonly Dictionary<RecordSchema, RecordNode> _records = [];

        public Node Build(AvroSchema schema) => schema switch
        {
            RecordSchema record => BuildRecord(record),
            EnumSchema => new EnumNode(),
            FixedSchema fixedSchema => new FixedNode(fixedSchema.Size),
            ArraySchema array => new ArrayNode(Build(array.Items)),
            MapSchema map => new MapNode(Build(map.Values)),
            UnionSchema union => new UnionNode(union.Branches.ConvertAll(Build)),
            _ => new PrimitiveNode(schema.Type),
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
            node.Fields = record.Fields.ConvertAll(f => Build(f.Schema));
            return node;
        }
    }

    private sealed class PrimitiveNode(AvroSchemaType type) : Node
    {
        public override int Compare(ref Inputs inputs)
        {
            switch (type)
            {
                case AvroSchemaType.Null:
                    return 0;
                case AvroSchemaType.Boolean:
                    return inputs.X.ReadBoolean().CompareTo(inputs.Y.ReadBoolean());
                case AvroSchemaType.Int:
                    return Math.Sign(inputs.X.ReadInt().CompareTo(inputs.Y.ReadInt()));
                case AvroSchemaType.Long:
                    return Math.Sign(inputs.X.ReadLong().CompareTo(inputs.Y.ReadLong()));
                case AvroSchemaType.Float:
                    return SortOrder.CompareSingle(inputs.X.ReadFloat(), inputs.Y.ReadFloat());
                case AvroSchemaType.Double:
                    return SortOrder.CompareDouble(inputs.X.ReadDouble(), inputs.Y.ReadDouble());
                default:
                    // bytes and string: both compare their bytes, UTF-8 for a string.
                    var left = inputs.X.ReadBytesSpan();
                    var right = inputs.Y.ReadBytesSpan();
                    return SortOrder.CompareBytes(left, right);
            }
        }

        public override void Skip(ref AvroReader reader, ref State state)
        {
            switch (type)
            {
                case AvroSchemaType.Null:
                    break;
                case AvroSchemaType.Boolean:
                    reader.ReadBoolean();
                    break;
                case AvroSchemaType.Int or AvroSchemaType.Long:
                    reader.SkipVarint();
                    break;
                case AvroSchemaType.Float:
                    reader.SkipRaw(4);
                    break;
                case AvroSchemaType.Double:
                    reader.SkipRaw(8);
                    break;
                default:
                    reader.SkipBytes();
                    break;
            }
        }
    }

    private sealed class RecordNode(RecordSchema schema) : Node
    {
        private readonly FieldOrder[] _orders = schema.Fields.ConvertAll(f => f.Order);

        public Node[] Fields { get; set; } = [];

        public override int Compare(ref Inputs inputs)
        {
            Enter(ref inputs.State);
            var fields = Fields;
            for (var i = 0; i < fields.Length; i++)
            {
                switch (_orders[i])
                {
                    case FieldOrder.Ignore:
                        fields[i].Skip(ref inputs.X, ref inputs.State);
                        fields[i].Skip(ref inputs.Y, ref inputs.State);
                        continue;
                    case var order:
                        var result = fields[i].Compare(ref inputs);
                        if (result != 0)
                        {
                            return order == FieldOrder.Descending ? -result : result;
                        }

                        continue;
                }
            }

            inputs.State.Depth--;
            return 0;
        }

        public override void Skip(ref AvroReader reader, ref State state)
        {
            Enter(ref state);
            foreach (var field in Fields)
            {
                field.Skip(ref reader, ref state);
            }

            state.Depth--;
        }

        private static void Enter(ref State state)
        {
            if (++state.Depth > state.MaxDepth)
            {
                throw new AvroDataException($"Records are nested more than {state.MaxDepth} levels deep (GenericDatumReaderOptions.MaxDepth).");
            }
        }
    }

    private sealed class EnumNode : Node
    {
        public override int Compare(ref Inputs inputs) =>
            Math.Sign(inputs.X.ReadEnum().CompareTo(inputs.Y.ReadEnum()));

        public override void Skip(ref AvroReader reader, ref State state) => reader.SkipVarint();
    }

    private sealed class FixedNode(int size) : Node
    {
        public override int Compare(ref Inputs inputs)
        {
            var left = inputs.X.ReadFixedSpan(size);
            var right = inputs.Y.ReadFixedSpan(size);
            return SortOrder.CompareBytes(left, right);
        }

        public override void Skip(ref AvroReader reader, ref State state) => reader.SkipRaw(size);
    }

    private sealed class ArrayNode(Node items) : Node
    {
        public override int Compare(ref Inputs inputs)
        {
            var left = inputs.X.ReadBlockCount(out _);
            var right = inputs.Y.ReadBlockCount(out _);
            while (left != 0 && right != 0)
            {
                inputs.State.TakeItem();
                var result = items.Compare(ref inputs);
                if (result != 0)
                {
                    return result;
                }

                if (--left == 0)
                {
                    left = inputs.X.ReadBlockCount(out _);
                }

                if (--right == 0)
                {
                    right = inputs.Y.ReadBlockCount(out _);
                }
            }

            // The array that ended first is the start of the other.
            return left == 0 ? (right == 0 ? 0 : -1) : 1;
        }

        public override void Skip(ref AvroReader reader, ref State state)
        {
            for (var count = reader.ReadBlockCount(out var size); count != 0; count = reader.ReadBlockCount(out size))
            {
                if (size >= 0)
                {
                    reader.SkipRaw(size);
                    continue;
                }

                for (; count > 0; count--)
                {
                    state.TakeItem();
                    items.Skip(ref reader, ref state);
                }
            }
        }
    }

    private sealed class MapNode(Node values) : Node
    {
        public override int Compare(ref Inputs inputs) => throw new AvroException(SortOrder.MapsMessage);

        public override void Skip(ref AvroReader reader, ref State state)
        {
            for (var count = reader.ReadBlockCount(out var size); count != 0; count = reader.ReadBlockCount(out size))
            {
                if (size >= 0)
                {
                    reader.SkipRaw(size);
                    continue;
                }

                for (; count > 0; count--)
                {
                    state.TakeItem();
                    reader.SkipBytes();
                    values.Skip(ref reader, ref state);
                }
            }
        }
    }

    private sealed class UnionNode(Node[] branches) : Node
    {
        public override int Compare(ref Inputs inputs)
        {
            var left = Index(ref inputs.X);
            var right = Index(ref inputs.Y);
            return left == right ? branches[left].Compare(ref inputs) : Math.Sign(left.CompareTo(right));
        }

        public override void Skip(ref AvroReader reader, ref State state) => branches[Index(ref reader)].Skip(ref reader, ref state);

        private int Index(ref AvroReader reader)
        {
            var index = reader.ReadUnionIndex();
            return (uint)index < (uint)branches.Length
                ? index
                : throw new AvroDataException($"Invalid union index {index}: the union has {branches.Length} branches.");
        }
    }
}
