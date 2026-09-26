using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Reads Avro binary data into <see cref="AvroValue"/>s for one schema. The schema is compiled once into a tree of
/// typed reader nodes, so reading does not inspect the schema; instances are cached per schema and are thread-safe.
/// </summary>
/// <remarks>
/// This reader expects data written with the same schema. Reading data written with a different schema version
/// (schema resolution) arrives in milestone M3.
/// </remarks>
public sealed class GenericDatumReader
{
    // Items pre-allocated for an array or map before any are read; larger blocks grow as items arrive, so a block
    // count alone never causes a large allocation.
    private const int PreallocationLimit = 1024;

    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumReader> s_cache = new();

    private readonly ReaderNode _root;
    private readonly GenericDatumReaderOptions _options;

    private GenericDatumReader(AvroSchema schema, GenericDatumReaderOptions options)
    {
        Schema = schema;
        _options = options;
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema the data was written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>
    /// Gets the reader for <paramref name="schema"/>, compiling it on first use. Readers with the default options are
    /// cached per schema; readers with custom options are created each time, so keep and reuse them.
    /// </summary>
    /// <param name="schema">The schema the data was written with.</param>
    /// <param name="options">Limits for malformed input, or <see langword="null"/> for <see cref="GenericDatumReaderOptions.Default"/>.</param>
    public static GenericDatumReader Create(AvroSchema schema, GenericDatumReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return options is null || ReferenceEquals(options, GenericDatumReaderOptions.Default)
            ? s_cache.GetValue(schema, static s => new GenericDatumReader(s, GenericDatumReaderOptions.Default))
            : new GenericDatumReader(schema, options);
    }

    /// <summary>Reads one value.</summary>
    /// <param name="reader">The source.</param>
    /// <exception cref="AvroDataException">The data is malformed or does not match the schema.</exception>
    public AvroValue Read(ref AvroReader reader)
    {
        var state = new ReadState(_options);
        return _root.Read(ref reader, ref state);
    }

    /// <summary>Reads one value from contiguous data.</summary>
    /// <param name="data">The encoded value.</param>
    public AvroValue Read(ReadOnlySpan<byte> data)
    {
        var reader = new AvroReader(data);
        return Read(ref reader);
    }

    private abstract class ReaderNode
    {
        /// <summary>Gets the smallest number of bytes a value of this node can occupy.</summary>
        public abstract int MinimumSize { get; }

        public abstract AvroValue Read(ref AvroReader reader, ref ReadState state);
    }

    /// <summary>Per-read limits: the current record depth and the remaining budget of zero-size items.</summary>
    [StructLayout(LayoutKind.Auto)]
    private struct ReadState(GenericDatumReaderOptions options)
    {
        public readonly int MaxDepth = options.MaxDepth;
        public int Depth;
        public long ZeroSizeItemsLeft = options.MaxZeroSizeItems;
    }

    private sealed class Builder
    {
        // Schemas compare by reference.
        private readonly Dictionary<RecordSchema, RecordNode> _records = [];

        public ReaderNode Build(AvroSchema schema) => schema switch
        {
            RecordSchema record => BuildRecord(record),
            EnumSchema enumSchema => new EnumNode(enumSchema),
            FixedSchema fixedSchema => new FixedNode(fixedSchema),
            ArraySchema array => new ArrayNode(Build(array.Items)),
            MapSchema map => new MapNode(Build(map.Values)),
            UnionSchema union => new UnionNode(union.Branches.ConvertAll(Build)),
            _ => schema.Type switch
            {
                AvroSchemaType.Null => NullNode.Instance,
                AvroSchemaType.Boolean => BooleanNode.Instance,
                AvroSchemaType.Int => IntNode.Instance,
                AvroSchemaType.Long => LongNode.Instance,
                AvroSchemaType.Float => FloatNode.Instance,
                AvroSchemaType.Double => DoubleNode.Instance,
                AvroSchemaType.Bytes => BytesNode.Instance,
                _ => StringNode.Instance,
            },
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

            // A recursive reference to this record still reads 0 here, so the result is a safe lower bound.
            long size = 0;
            foreach (var field in node.Fields)
            {
                size += field.MinimumSize;
            }

            node.Size = (int)Math.Min(size, int.MaxValue);
            return node;
        }
    }

    private sealed class NullNode : ReaderNode
    {
        public static NullNode Instance { get; } = new();

        public override int MinimumSize => 0;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => AvroValue.Null;
    }

    private sealed class BooleanNode : ReaderNode
    {
        public static BooleanNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadBoolean();
    }

    private sealed class IntNode : ReaderNode
    {
        public static IntNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadInt();
    }

    private sealed class LongNode : ReaderNode
    {
        public static LongNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadLong();
    }

    private sealed class FloatNode : ReaderNode
    {
        public static FloatNode Instance { get; } = new();

        public override int MinimumSize => sizeof(float);

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadFloat();
    }

    private sealed class DoubleNode : ReaderNode
    {
        public static DoubleNode Instance { get; } = new();

        public override int MinimumSize => sizeof(double);

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadDouble();
    }

    private sealed class BytesNode : ReaderNode
    {
        public static BytesNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadBytes();
    }

    private sealed class StringNode : ReaderNode
    {
        public static StringNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => reader.ReadString();
    }

    private sealed class RecordNode(RecordSchema schema) : ReaderNode
    {
        public ReaderNode[] Fields { get; set; } = [];

        /// <summary>Gets or sets the sum of the fields' minimum sizes; a lower bound, set once the fields are built.</summary>
        public int Size { get; set; }

        public override int MinimumSize => Size;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            if (++state.Depth > state.MaxDepth)
            {
                throw new AvroDataException($"Records are nested more than {state.MaxDepth} levels deep (GenericDatumReaderOptions.MaxDepth).");
            }

            var fields = Fields;
            var record = new GenericRecord(schema, fields.Length);
            for (var i = 0; i < fields.Length; i++)
            {
                record.ValueAt(i) = fields[i].Read(ref reader, ref state);
            }

            state.Depth--;
            return record;
        }
    }

    private sealed class EnumNode(EnumSchema schema) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            var ordinal = reader.ReadEnum();
            if ((uint)ordinal >= (uint)schema.Symbols.Count)
            {
                throw new AvroDataException($"Enum ordinal {ordinal} is out of range for '{schema.FullName}' ({schema.Symbols.Count} symbols).");
            }

            return AvroValue.FromEnumUnchecked(schema, ordinal);
        }
    }

    private sealed class FixedNode(FixedSchema schema) : ReaderNode
    {
        public override int MinimumSize => schema.Size;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) =>
            new GenericFixed(schema, reader.ReadFixedSpan(schema.Size).ToArray());
    }

    private sealed class ArrayNode(ReaderNode items) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            var list = new List<AvroValue>();
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                CheckBlockCount(ref reader, ref state, count, list.Count, items.MinimumSize);
                var n = (int)count;
                if (list.Count == 0)
                {
                    list.Capacity = Math.Min(n, PreallocationLimit);
                }

                if (items is LongNode)
                {
                    ReadLongItems(ref reader, list, n);
                }
                else if (items is IntNode)
                {
                    ReadIntItems(ref reader, list, n);
                }
                else if (items is DoubleNode)
                {
                    ReadDoubleItems(ref reader, list, n);
                }
                else if (items is FloatNode)
                {
                    ReadFloatItems(ref reader, list, n);
                }
                else
                {
                    for (var i = 0; i < n; i++)
                    {
                        list.Add(items.Read(ref reader, ref state));
                    }
                }
            }

            return AvroValue.FromArray(list);
        }

        // Bulk paths for arrays of int and long: the reader decodes whole runs of small values at once.
        private static void ReadLongItems(ref AvroReader reader, List<AvroValue> list, int count)
        {
            var buffer = ArrayPool<long>.Shared.Rent(Math.Min(count, 1024));
            try
            {
                while (count > 0)
                {
                    var chunk = buffer.AsSpan(0, Math.Min(count, buffer.Length));
                    reader.ReadLongs(chunk);
                    AddRange(list, chunk);
                    count -= chunk.Length;
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(buffer);
            }
        }

        private static void AddRange(List<AvroValue> list, ReadOnlySpan<long> values)
        {
#if NET8_0_OR_GREATER
            // Grow the list once and write straight into its backing array.
            var start = list.Count;
            CollectionsMarshal.SetCount(list, start + values.Length);
            var target = CollectionsMarshal.AsSpan(list).Slice(start, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                target[i] = values[i];
            }
#else
            foreach (var value in values)
            {
                list.Add(value);
            }
#endif
        }

        private static void AddRange(List<AvroValue> list, ReadOnlySpan<int> values)
        {
#if NET8_0_OR_GREATER
            // Grow the list once and write straight into its backing array.
            var start = list.Count;
            CollectionsMarshal.SetCount(list, start + values.Length);
            var target = CollectionsMarshal.AsSpan(list).Slice(start, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                target[i] = values[i];
            }
#else
            foreach (var value in values)
            {
                list.Add(value);
            }
#endif
        }

        private static void AddRange(List<AvroValue> list, ReadOnlySpan<double> values)
        {
#if NET8_0_OR_GREATER
            // Grow the list once and write straight into its backing array.
            var start = list.Count;
            CollectionsMarshal.SetCount(list, start + values.Length);
            var target = CollectionsMarshal.AsSpan(list).Slice(start, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                target[i] = values[i];
            }
#else
            foreach (var value in values)
            {
                list.Add(value);
            }
#endif
        }

        private static void AddRange(List<AvroValue> list, ReadOnlySpan<float> values)
        {
#if NET8_0_OR_GREATER
            // Grow the list once and write straight into its backing array.
            var start = list.Count;
            CollectionsMarshal.SetCount(list, start + values.Length);
            var target = CollectionsMarshal.AsSpan(list).Slice(start, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                target[i] = values[i];
            }
#else
            foreach (var value in values)
            {
                list.Add(value);
            }
#endif
        }

        // Fixed-width items: one bounds check and, on little-endian hardware, one copy per chunk.
        private static void ReadDoubleItems(ref AvroReader reader, List<AvroValue> list, int count)
        {
            var buffer = ArrayPool<double>.Shared.Rent(Math.Min(count, 1024));
            try
            {
                while (count > 0)
                {
                    var chunk = buffer.AsSpan(0, Math.Min(count, buffer.Length));
                    reader.ReadDoubles(chunk);
                    AddRange(list, chunk);
                    count -= chunk.Length;
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(buffer);
            }
        }

        private static void ReadFloatItems(ref AvroReader reader, List<AvroValue> list, int count)
        {
            var buffer = ArrayPool<float>.Shared.Rent(Math.Min(count, 1024));
            try
            {
                while (count > 0)
                {
                    var chunk = buffer.AsSpan(0, Math.Min(count, buffer.Length));
                    reader.ReadFloats(chunk);
                    AddRange(list, chunk);
                    count -= chunk.Length;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
        private static void ReadIntItems(ref AvroReader reader, List<AvroValue> list, int count)
        {
            var buffer = ArrayPool<int>.Shared.Rent(Math.Min(count, 1024));
            try
            {
                while (count > 0)
                {
                    var chunk = buffer.AsSpan(0, Math.Min(count, buffer.Length));
                    reader.ReadInts(chunk);
                    AddRange(list, chunk);
                    count -= chunk.Length;
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buffer);
            }
        }
    }

    private sealed class MapNode(ReaderNode values) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            Dictionary<string, AvroValue>? map = null;
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                // Each entry has at least a key length byte.
                CheckBlockCount(ref reader, ref state, count, map?.Count ?? 0, 1 + values.MinimumSize);
                map ??= new Dictionary<string, AvroValue>((int)Math.Min(count, PreallocationLimit), StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var key = reader.ReadString();
                    map[key] = values.Read(ref reader, ref state);
                }
            }

            map ??= new Dictionary<string, AvroValue>(StringComparer.Ordinal);

            return AvroValue.FromMap(map);
        }
    }

    private sealed class UnionNode(ReaderNode[] branches) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            var index = reader.ReadUnionIndex();
            if ((uint)index >= (uint)branches.Length)
            {
                throw new AvroDataException($"Union branch index {index} is out of range ({branches.Length} branches).");
            }

            return branches[index].Read(ref reader, ref state);
        }
    }

    /// <summary>
    /// Rejects a block count before anything is allocated: items of at least <paramref name="minimumItemSize"/> bytes
    /// must fit in the remaining input; zero-size items draw from the per-read budget; and the collection may not
    /// exceed the largest array .NET can hold.
    /// </summary>
    private static void CheckBlockCount(ref AvroReader reader, ref ReadState state, long count, int itemsSoFar, int minimumItemSize)
    {
        if (minimumItemSize > 0)
        {
            if (count > reader.BytesRemaining / minimumItemSize)
            {
                throw new AvroDataException($"Block count {count} is larger than the remaining input can hold.");
            }
        }
        else
        {
            state.ZeroSizeItemsLeft -= count;
            if (state.ZeroSizeItemsLeft < 0)
            {
                throw new AvroDataException($"The input declares more zero-size items than allowed (GenericDatumReaderOptions.MaxZeroSizeItems).");
            }
        }

        if (itemsSoFar + count > MaxCollectionCount)
        {
            throw new AvroDataException($"The collection would hold more than {MaxCollectionCount} items.");
        }
    }

    // The largest element count of a .NET array (Array.MaxLength on net6+).
    private const int MaxCollectionCount = 0x7FFFFFC7;
}
