using System;
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
/// <see cref="Create(AvroSchema, GenericDatumReaderOptions?)"/> reads data with the schema it was written with.
/// <see cref="Create(AvroSchema, AvroSchema, GenericDatumReaderOptions?)"/> reads data written with one schema version
/// as another (schema resolution), following the specification's rules.
/// </remarks>
public sealed partial class GenericDatumReader
{
    // Items pre-allocated for an array or map before any are read; larger blocks grow as items arrive, so a block
    // count alone never causes a large allocation.
    private const int PreallocationLimit = 1024;

    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumReader> s_cache = new();

    private readonly ReaderNode _root;
    // Built once: each read starts from a copy (#129 added fields that the options would otherwise compute per read).
    private readonly ReadState _initialState;

    // The state reads with the default options start from, for the transcoder and the plans' skip steps.
    private static readonly ReadState s_defaultState = new(GenericDatumReaderOptions.Default);

    private GenericDatumReader(AvroSchema schema, GenericDatumReaderOptions options)
    {
        Schema = schema;
        ReaderSchema = schema;
        _initialState = new ReadState(options);
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema the data was written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the schema that values are read as: the same as <see cref="Schema"/> unless resolving between versions.</summary>
    public AvroSchema ReaderSchema { get; }

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
        var state = _initialState;
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

        /// <summary>
        /// Gets what a value of this node costs against the zero-size budget when <see cref="MinimumSize"/> is 0:
        /// the number of values reading it creates (see <see cref="ZeroSizeValues"/>).
        /// </summary>
        public virtual long ZeroSizeCost => 1;

        public abstract AvroValue Read(ref AvroReader reader, ref ReadState state);
    }

    /// <summary>
    /// Per-read limits: the current record depth, the current nesting of every kind, and the remaining budget of
    /// zero-size items.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private struct ReadState(GenericDatumReaderOptions options)
    {
        public readonly int MaxDepth = options.MaxDepth;
        public int Depth;

        // Arrays and maps nest too, and a hostile writer schema can nest them between records (#129): the total is
        // bounded separately, at NestingPerDepth times MaxDepth, so the stack is bounded however the nesting is built.
        // Unions aren't counted: one cannot hold another, so the arrays, maps and records in them count.
        public readonly int MaxNesting = (int)Math.Min((long)options.MaxDepth * NestingPerDepth, int.MaxValue);
        public int Nesting;
        public long ZeroSizeItemsLeft = options.MaxZeroSizeItems;

        // The nesting level at which the limit and the stack are next checked (EnterNesting).
        public int NextCheck = NextCheckAfter(0, (int)Math.Min((long)options.MaxDepth * NestingPerDepth, int.MaxValue));
    }

    // Levels of any kind (records, arrays, maps, unions) allowed per record level of MaxDepth.
    private const int NestingPerDepth = 8;

    // The nesting levels between stack checks.
    private const int StackCheckInterval = 16;

    /// <summary>
    /// Enters an array or map: counts it against the nesting limit, and checks that the thread has stack left
    /// each time the nesting reaches <see cref="StackCheckInterval"/> levels past the deepest level checked.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnterNesting(ref ReadState state)
    {
        // One comparison covers both the limit and the stack check: NextCheck is at most MaxNesting + 1.
        if (++state.Nesting >= state.NextCheck)
        {
            CheckNesting(ref state);
        }
    }

    /// <summary>Enters a record: counts it against the record depth and as nesting (see <see cref="EnterNesting"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnterRecord(ref ReadState state)
    {
        if (++state.Depth > state.MaxDepth)
        {
            ThrowRecordsTooDeep(state.MaxDepth);
        }

        EnterNesting(ref state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRecordsTooDeep(int maxDepth) =>
        throw new AvroDataException($"Records are nested more than {maxDepth} levels deep (GenericDatumReaderOptions.MaxDepth).");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExitRecord(ref ReadState state)
    {
        state.Depth--;
        state.Nesting--;
    }

    // Checked per record, the stack cost 5-8 ns a record (#129). Levels above the deepest one checked already had
    // their stack, and the levels between checks take a few KB, well within the margin the check leaves.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckNesting(ref ReadState state)
    {
        if (state.Nesting > state.MaxNesting)
        {
            ThrowNestedTooDeep(state.MaxNesting);
        }

        EnsureStack();
        state.NextCheck = NextCheckAfter(state.Nesting, state.MaxNesting);
    }

    // The next nesting level to check at: StackCheckInterval levels deeper, or just past the limit.
    private static int NextCheckAfter(int nesting, int maxNesting) => (int)Math.Min((long)nesting + StackCheckInterval, (long)maxNesting + 1);

    private static void EnsureStack()
    {
#if NET
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new AvroDataException("The value is nested too deeply for the thread's stack.");
        }
#else
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException ex)
        {
            throw new AvroDataException("The value is nested too deeply for the thread's stack.", ex);
        }
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNestedTooDeep(int maxNesting) =>
        throw new AvroDataException($"Values are nested more than {maxNesting} levels deep, counting arrays, maps and records ({NestingPerDepth} times GenericDatumReaderOptions.MaxDepth).");

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
        private long _zeroSizeCost;

        public ReaderNode[] Fields { get; set; } = [];

        /// <summary>Gets or sets the sum of the fields' minimum sizes; a lower bound, set once the fields are built.</summary>
        public int Size { get; set; }

        public override int MinimumSize => Size;

        // A record of many null fields takes no bytes, but creates a value for each field (#129).
        public override long ZeroSizeCost => _zeroSizeCost != 0 ? _zeroSizeCost : _zeroSizeCost = Math.Max(1, ZeroSizeValues.Count(schema));

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            EnterRecord(ref state);
            var fields = Fields;
            var record = new GenericRecord(schema, fields.Length);
            for (var i = 0; i < fields.Length; i++)
            {
                record.ValueAt(i) = fields[i].Read(ref reader, ref state);
            }

            ExitRecord(ref state);
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
            // Arrays of primitive items are read in bulk into an array of the primitives themselves.
            switch (items)
            {
                case LongNode:
                    return AvroValue.FromInt64Array(ReadBulkItems<long, LongItems>(ref reader, ref state));
                case IntNode:
                    return AvroValue.FromInt32Array(ReadBulkItems<int, IntItems>(ref reader, ref state));
                case DoubleNode:
                    return AvroValue.FromDoubleArray(ReadBulkItems<double, DoubleItems>(ref reader, ref state));
                case FloatNode:
                    return AvroValue.FromSingleArray(ReadBulkItems<float, FloatItems>(ref reader, ref state));
                case BooleanNode:
                    return AvroValue.FromBooleanArray(ReadBulkItems<bool, BooleanItems>(ref reader, ref state));
            }

            EnterNesting(ref state);
            var list = new List<AvroValue>();
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                CheckBlockCount(ref reader, ref state, count, list.Count, items.MinimumSize, items.ZeroSizeCost);
                var n = (int)count;
                if (list.Count == 0)
                {
                    list.Capacity = Math.Min(n, PreallocationLimit);
                }

                for (var i = 0; i < n; i++)
                {
                    list.Add(items.Read(ref reader, ref state));
                }
            }

            state.Nesting--;
            return AvroValue.FromArray(list);
        }

        // Bulk paths for arrays of boolean, int, long, float and double: each block is decoded straight into the
        // result (runs of small varints together; fixed-width items with one copy on little-endian hardware).
        // TItems is a struct, so each element type gets its own specialized code, with no delegate call per item.
        // CheckBlockCount has checked that the input holds the whole block, which bounds each allocation.
        private ReadOnlyMemory<T> ReadBulkItems<T, TItems>(ref AvroReader reader, ref ReadState state)
            where T : unmanaged
            where TItems : struct, IBulkItems<T>
        {
            var values = Array.Empty<T>();
            var length = 0;
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                CheckBlockCount(ref reader, ref state, count, length, items.MinimumSize);
                var n = (int)count;
                if (values.Length - length < n)
                {
                    Array.Resize(ref values, Math.Max(length + n, values.Length * 2));
                }

                default(TItems).Read(ref reader, values.AsSpan(length, n));
                length += n;
            }

            return values.AsMemory(0, length);
        }
    }

    /// <summary>How one primitive element type is read in bulk.</summary>
    private interface IBulkItems<T>
        where T : unmanaged
    {
        void Read(ref AvroReader reader, Span<T> destination);
    }

    private readonly struct LongItems : IBulkItems<long>
    {
        public void Read(ref AvroReader reader, Span<long> destination) => reader.ReadLongs(destination);
    }

    private readonly struct IntItems : IBulkItems<int>
    {
        public void Read(ref AvroReader reader, Span<int> destination) => reader.ReadInts(destination);
    }

    private readonly struct DoubleItems : IBulkItems<double>
    {
        public void Read(ref AvroReader reader, Span<double> destination) => reader.ReadDoubles(destination);
    }

    private readonly struct FloatItems : IBulkItems<float>
    {
        public void Read(ref AvroReader reader, Span<float> destination) => reader.ReadFloats(destination);
    }

    private readonly struct BooleanItems : IBulkItems<bool>
    {
        public void Read(ref AvroReader reader, Span<bool> destination) => reader.ReadBooleans(destination);
    }

    private sealed class MapNode(ReaderNode values) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            EnterNesting(ref state);
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
            state.Nesting--;
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

            // Not counted as nesting: a union cannot hold a union, so the arrays, maps and records in it count (#129).
            return branches[index].Read(ref reader, ref state);
        }
    }

    /// <summary>
    /// Rejects a block count before anything is allocated: items of at least <paramref name="minimumItemSize"/> bytes
    /// must fit in the remaining input; zero-size items draw <paramref name="zeroSizeCost"/> each from the per-read
    /// budget; and the collection may not exceed the largest array .NET can hold.
    /// </summary>
    private static void CheckBlockCount(ref AvroReader reader, ref ReadState state, long count, int itemsSoFar, int minimumItemSize, long zeroSizeCost = 1)
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
            if (count > state.ZeroSizeItemsLeft / zeroSizeCost)
            {
                throw new AvroDataException($"The input declares more zero-size items than allowed (GenericDatumReaderOptions.MaxZeroSizeItems, which counts each item as the values it creates: one, plus one per field of each record in it).");
            }

            state.ZeroSizeItemsLeft -= count * zeroSizeCost;
        }

        if (itemsSoFar + count > MaxCollectionCount)
        {
            throw new AvroDataException($"The collection would hold more than {MaxCollectionCount} items.");
        }
    }

    // The largest element count of a .NET array (Array.MaxLength on net6+).
    private const int MaxCollectionCount = 0x7FFFFFC7;
}
