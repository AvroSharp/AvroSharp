using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// <summary>
    /// The largest number of zero-size items (for example <c>null</c>s) an array or map block may declare. Items
    /// that take at least one byte are bounded by the remaining input instead.
    /// </summary>
    public const int MaxZeroSizeItemsPerBlock = 1 << 24;

    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumReader> s_cache = new();

    private readonly ReaderNode _root;

    private GenericDatumReader(AvroSchema schema)
    {
        Schema = schema;
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema the data was written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the reader for <paramref name="schema"/>, compiling it on first use.</summary>
    /// <param name="schema">The schema the data was written with.</param>
    public static GenericDatumReader Create(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return s_cache.GetValue(schema, static s => new GenericDatumReader(s));
    }

    /// <summary>Reads one value.</summary>
    /// <param name="reader">The source.</param>
    /// <exception cref="AvroDataException">The data is malformed or does not match the schema.</exception>
    public AvroValue Read(ref AvroReader reader) => _root.Read(ref reader);

    /// <summary>Reads one value from contiguous data.</summary>
    /// <param name="data">The encoded value.</param>
    public AvroValue Read(ReadOnlySpan<byte> data)
    {
        var reader = new AvroReader(data);
        return _root.Read(ref reader);
    }

    private abstract class ReaderNode
    {
        /// <summary>Gets the smallest number of bytes a value of this node can occupy.</summary>
        public abstract int MinimumSize { get; }

        public abstract AvroValue Read(ref AvroReader reader);
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
            return node;
        }
    }

    private sealed class NullNode : ReaderNode
    {
        public static NullNode Instance { get; } = new();

        public override int MinimumSize => 0;

        public override AvroValue Read(ref AvroReader reader) => AvroValue.Null;
    }

    private sealed class BooleanNode : ReaderNode
    {
        public static BooleanNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader) => reader.ReadBoolean();
    }

    private sealed class IntNode : ReaderNode
    {
        public static IntNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader) => reader.ReadInt();
    }

    private sealed class LongNode : ReaderNode
    {
        public static LongNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader) => reader.ReadLong();
    }

    private sealed class FloatNode : ReaderNode
    {
        public static FloatNode Instance { get; } = new();

        public override int MinimumSize => sizeof(float);

        public override AvroValue Read(ref AvroReader reader) => reader.ReadFloat();
    }

    private sealed class DoubleNode : ReaderNode
    {
        public static DoubleNode Instance { get; } = new();

        public override int MinimumSize => sizeof(double);

        public override AvroValue Read(ref AvroReader reader) => reader.ReadDouble();
    }

    private sealed class BytesNode : ReaderNode
    {
        public static BytesNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader) => reader.ReadBytes();
    }

    private sealed class StringNode : ReaderNode
    {
        public static StringNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader) => reader.ReadString();
    }

    private sealed class RecordNode(RecordSchema schema) : ReaderNode
    {
        public ReaderNode[] Fields { get; set; } = [];

        // A lower bound; recursion through the record itself counts as zero.
        public override int MinimumSize => 0;

        public override AvroValue Read(ref AvroReader reader)
        {
            var record = new GenericRecord(schema);
            var fields = Fields;
            for (var i = 0; i < fields.Length; i++)
            {
                record[i] = fields[i].Read(ref reader);
            }

            return record;
        }
    }

    private sealed class EnumNode(EnumSchema schema) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader)
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

        public override AvroValue Read(ref AvroReader reader) =>
            new GenericFixed(schema, reader.ReadFixedSpan(schema.Size).ToArray());
    }

    private sealed class ArrayNode(ReaderNode items) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader)
        {
            var list = new List<AvroValue>();
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                CheckBlockCount(ref reader, count, items.MinimumSize);
                var n = (int)count;
                list.Capacity = Math.Max(list.Capacity, list.Count + n);
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
                        list.Add(items.Read(ref reader));
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
                    foreach (var value in chunk)
                    {
                        list.Add(value);
                    }

                    count -= chunk.Length;
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(buffer);
            }
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
                    foreach (var value in chunk)
                    {
                        list.Add(value);
                    }

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
                    foreach (var value in chunk)
                    {
                        list.Add(value);
                    }

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
                    foreach (var value in chunk)
                    {
                        list.Add(value);
                    }

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

        public override AvroValue Read(ref AvroReader reader)
        {
            var map = new Dictionary<string, AvroValue>(StringComparer.Ordinal);
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                // Each entry has at least a key length byte.
                CheckBlockCount(ref reader, count, 1 + values.MinimumSize);
                for (var i = 0; i < count; i++)
                {
                    var key = reader.ReadString();
                    map[key] = values.Read(ref reader);
                }
            }

            return AvroValue.FromMap(map);
        }
    }

    private sealed class UnionNode(ReaderNode[] branches) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader)
        {
            var index = reader.ReadUnionIndex();
            if ((uint)index >= (uint)branches.Length)
            {
                throw new AvroDataException($"Union branch index {index} is out of range ({branches.Length} branches).");
            }

            return branches[index].Read(ref reader);
        }
    }

    /// <summary>
    /// Rejects block counts the remaining input cannot hold, before anything is allocated: each item needs at least
    /// <paramref name="minimumItemSize"/> bytes, and zero-size items are capped at <see cref="MaxZeroSizeItemsPerBlock"/>.
    /// </summary>
    private static void CheckBlockCount(ref AvroReader reader, long count, int minimumItemSize)
    {
        var limit = minimumItemSize > 0 ? reader.BytesRemaining / minimumItemSize : MaxZeroSizeItemsPerBlock;
        if (count > limit || count > int.MaxValue)
        {
            throw new AvroDataException($"Block count {count} is larger than the remaining input can hold.");
        }
    }
}
