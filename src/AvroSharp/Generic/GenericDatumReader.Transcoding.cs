using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AvroSharp.Buffers;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Generic;

/// <summary>
/// Resolution into bytes: reads data of a writer schema and writes the same values in a reader schema's encoding,
/// following the same rules as the resolving reader, without materializing generic values. Generated types use it to
/// read data of another schema version with their own reader.
/// </summary>
public sealed partial class GenericDatumReader
{
    // Keyed by the reader's schema, then the writer's (#129): each value references both schemas, and a value lives as
    // long as its key, so keyed the other way round a long-lived reader schema kept every writer schema alive.
    private static readonly ConditionalWeakTable<AvroSchema, ConditionalWeakTable<AvroSchema, Transcoder>> s_transcoders = new();

    /// <summary>Gets the cached transcoder from <paramref name="writerSchema"/>'s encoding to <paramref name="readerSchema"/>'s.</summary>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved.</exception>
    internal static Transcoder GetTranscoder(AvroSchema writerSchema, AvroSchema readerSchema)
    {
        var byWriter = s_transcoders.GetValue(readerSchema, static _ => new ConditionalWeakTable<AvroSchema, Transcoder>());
        return byWriter.GetValue(writerSchema, writer => new Transcoder(writer, readerSchema));
    }

    /// <summary>Rewrites one value from a writer schema's encoding into a reader schema's.</summary>
    internal sealed class Transcoder
    {
        [ThreadStatic]
        private static ScratchBuffers? s_scratch;

        // The thread's scratch buffers, created on first use.
        private static ScratchBuffers ThreadScratch() => s_scratch ??= new ScratchBuffers();

        private readonly TranscodeNode _root;

        internal Transcoder(AvroSchema writerSchema, AvroSchema readerSchema, string path = "$") => _root = new TranscodingBuilder().Build(writerSchema, readerSchema, path);

        /// <summary>Transcodes one value into the per-thread buffer of <see cref="AvroGeneratedCode"/> and returns it.</summary>
        public System.ReadOnlySpan<byte> TranscodeToBuffer(ref AvroReader reader)
        {
            var buffer = AvroGeneratedCode.ResolvedBuffer();
            Transcode(ref reader, buffer);
            return buffer.WrittenSpan;
        }

        public void Transcode(ref AvroReader reader, IBufferWriter<byte> output)
        {
            var state = ReadState.ForDefaultOptions();
            var scratch = ThreadScratch();
            var writer = new AvroWriter(output);
            _root.Transcode(ref reader, ref writer, ref state, scratch);
            writer.Flush();
        }
    }

    /// <summary>Buffers for record fields that the reader orders differently from the writer, reused within one value.</summary>
    private sealed class ScratchBuffers
    {
        private readonly Stack<PooledBufferWriter> _free = new();

        public PooledBufferWriter Rent() => _free.Count > 0 ? _free.Pop() : new PooledBufferWriter(256);

        public void Return(PooledBufferWriter buffer)
        {
            buffer.Clear();
            _free.Push(buffer);
        }
    }

    private abstract class TranscodeNode
    {
        /// <summary>Gets the smallest number of bytes the writer's encoding of a value takes.</summary>
        public abstract int MinimumSize { get; }

        /// <summary>Gets what a value costs against the zero-size budget when <see cref="MinimumSize"/> is 0.</summary>
        public virtual long ZeroSizeCost => 1;

        public abstract void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch);
    }

    private sealed class TranscodingBuilder
    {
        private readonly Dictionary<(RecordSchema Writer, RecordSchema Reader), RecordTranscodeNode> _records = [];
        private readonly ResolvingBuilder _skips = new();
        private readonly Builder _identity = new();

        public TranscodeNode Build(AvroSchema writer, AvroSchema reader, string path)
        {
            if (writer is UnionSchema writerUnion)
            {
                return new WriterUnionTranscodeNode([.. writerUnion.Branches.Select(branch => BuildOrDefer(branch, reader, path))]);
            }

            if (reader is UnionSchema readerUnion)
            {
                var branch = ResolvingBuilder.BestBranch(writer, readerUnion)
                    ?? throw ResolvingBuilder.Incompatible(writer, reader, path, "no branch of the reader's union matches");
                return new ReaderBranchTranscodeNode(IndexOf(readerUnion, branch), Build(writer, branch, path));
            }

            return BuildNonUnion(writer, reader, path);
        }

        private static int IndexOf(UnionSchema union, AvroSchema branch)
        {
            for (var i = 0; i < union.Branches.Count; i++)
            {
                if (ReferenceEquals(union.Branches[i], branch))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("The branch is not in the union.");
        }

        private TranscodeNode BuildOrDefer(AvroSchema writer, AvroSchema reader, string path)
        {
            try
            {
                return Build(writer, reader, path);
            }
            catch (AvroSchemaException ex)
            {
                return new ErrorTranscodeNode(ex.Message, _identity.Build(writer).MinimumSize);
            }
        }

        private TranscodeNode BuildNonUnion(AvroSchema writer, AvroSchema reader, string path)
        {
            return (writer, reader) switch
            {
                (RecordSchema w, RecordSchema r) when ResolvingBuilder.NamesMatch(w, r) => BuildRecord(w, r, path),
                (EnumSchema w, EnumSchema r) when ResolvingBuilder.NamesMatch(w, r) => new EnumTranscodeNode(w, r, ResolvingBuilder.EnumMap(w, r)),
                (FixedSchema w, FixedSchema r) when ResolvingBuilder.NamesMatch(w, r) => w.Size == r.Size
                    ? new CopyFixedNode(w.Size)
                    : throw ResolvingBuilder.Incompatible(writer, reader, path, $"the fixed sizes differ ({w.Size} and {r.Size})"),
                (ArraySchema w, ArraySchema r) => new BlocksTranscodeNode(Build(w.Items, r.Items, path + "[]"), isMap: false),
                (MapSchema w, MapSchema r) => new BlocksTranscodeNode(Build(w.Values, r.Values, path + "{}"), isMap: true),
                (PrimitiveSchema, PrimitiveSchema) => Primitive(writer.Type, reader.Type) ?? throw ResolvingBuilder.Incompatible(writer, reader, path, "the types differ and no promotion applies"),
                _ => throw ResolvingBuilder.Incompatible(writer, reader, path, writer is NamedSchema && reader is NamedSchema ? "the names differ" : "the types differ"),
            };
        }

        private static PrimitiveTranscodeNode? Primitive(AvroSchemaType writer, AvroSchemaType reader) => (writer, reader) switch
        {
            (AvroSchemaType.Null, AvroSchemaType.Null) => PrimitiveTranscodeNode.Null,
            (AvroSchemaType.Boolean, AvroSchemaType.Boolean) => PrimitiveTranscodeNode.Boolean,
            (AvroSchemaType.Int, AvroSchemaType.Int) => PrimitiveTranscodeNode.Int,
            // Read as an int, as the resolving reader does: an int's varint is decoded to 32 bits.
            (AvroSchemaType.Int, AvroSchemaType.Long) => PrimitiveTranscodeNode.IntToLong,
            (AvroSchemaType.Long, AvroSchemaType.Long) => PrimitiveTranscodeNode.Long,
            (AvroSchemaType.Int, AvroSchemaType.Float) => PrimitiveTranscodeNode.IntToFloat,
            (AvroSchemaType.Int, AvroSchemaType.Double) => PrimitiveTranscodeNode.IntToDouble,
            (AvroSchemaType.Long, AvroSchemaType.Float) => PrimitiveTranscodeNode.LongToFloat,
            (AvroSchemaType.Long, AvroSchemaType.Double) => PrimitiveTranscodeNode.LongToDouble,
            (AvroSchemaType.Float, AvroSchemaType.Float) => PrimitiveTranscodeNode.Float,
            (AvroSchemaType.Float, AvroSchemaType.Double) => PrimitiveTranscodeNode.FloatToDouble,
            (AvroSchemaType.Double, AvroSchemaType.Double) => PrimitiveTranscodeNode.Double,
            // string and bytes share an encoding; the generated reader validates UTF-8 when it reads a string.
            (AvroSchemaType.String or AvroSchemaType.Bytes, AvroSchemaType.String or AvroSchemaType.Bytes) => PrimitiveTranscodeNode.Bytes,
            _ => null,
        };

        private RecordTranscodeNode BuildRecord(RecordSchema writer, RecordSchema reader, string path)
        {
            if (_records.TryGetValue((writer, reader), out var existing))
            {
                return existing;
            }

            var node = new RecordTranscodeNode(reader.Fields.Count);
            _records.Add((writer, reader), node);

            var assigned = new bool[reader.Fields.Count];
            var steps = new List<(int Target, TranscodeNode Node)>(writer.Fields.Count);
            var size = 0L;
            foreach (var writerField in writer.Fields)
            {
                var readerField = _skips.FindField(reader, writerField.Name);
                TranscodeNode child;
                if (readerField is null || assigned[readerField.Position])
                {
                    child = new SkipTranscodeNode(_skips.BuildSkipNode(writerField.Schema));
                    steps.Add((-1, child));
                }
                else
                {
                    assigned[readerField.Position] = true;
                    child = Build(writerField.Schema, readerField.Schema, path + "." + readerField.Name);
                    steps.Add((readerField.Position, child));
                }

                size += child.MinimumSize;
            }

            var defaults = new byte[]?[reader.Fields.Count];
            foreach (var readerField in reader.Fields)
            {
                if (assigned[readerField.Position])
                {
                    continue;
                }

                var value = readerField.DefaultValue
                    ?? throw new AvroSchemaException(
                        $"At {path}: the reader's field '{reader.FullName}.{readerField.Name}' is not in the writer's schema and has no default value.");
                defaults[readerField.Position] = GenericDatumWriter.Create(readerField.Schema).WriteToArray(GenericDatumJsonReader.ReadDefault(readerField.Schema, value));
            }

            node.Steps = [.. steps];
            node.Defaults = defaults;
            node.Size = (int)Math.Min(size, int.MaxValue);

            // The writer's matched fields in the reader's order: stream them, writing defaults in the gaps.
            var targets = steps.Where(s => s.Target >= 0).Select(s => s.Target).ToList();
            node.InOrder = targets.Zip(targets.Skip(1), (a, b) => a < b).All(increasing => increasing);
            return node;
        }
    }

    private sealed class PrimitiveTranscodeNode : TranscodeNode
    {
        private delegate void Copy(ref AvroReader reader, ref AvroWriter writer);

        private readonly Copy _copy;
        private readonly int _minimumSize;

        private PrimitiveTranscodeNode(int minimumSize, Copy copy)
        {
            _minimumSize = minimumSize;
            _copy = copy;
        }

        public static PrimitiveTranscodeNode Null { get; } = new(0, static (ref r, ref w) => { });

        public static PrimitiveTranscodeNode Boolean { get; } = new(1, static (ref r, ref w) => w.WriteBoolean(r.ReadBoolean()));

        public static PrimitiveTranscodeNode Int { get; } = new(1, static (ref r, ref w) => w.WriteInt(r.ReadInt()));

        public static PrimitiveTranscodeNode Long { get; } = new(1, static (ref r, ref w) => w.WriteLong(r.ReadLong()));

        public static PrimitiveTranscodeNode IntToLong { get; } = new(1, static (ref r, ref w) => w.WriteLong(r.ReadInt()));

        public static PrimitiveTranscodeNode IntToFloat { get; } = new(1, static (ref r, ref w) => w.WriteFloat(r.ReadInt()));

        public static PrimitiveTranscodeNode IntToDouble { get; } = new(1, static (ref r, ref w) => w.WriteDouble(r.ReadInt()));

        public static PrimitiveTranscodeNode LongToFloat { get; } = new(1, static (ref r, ref w) => w.WriteFloat(r.ReadLong()));

        public static PrimitiveTranscodeNode LongToDouble { get; } = new(1, static (ref r, ref w) => w.WriteDouble(r.ReadLong()));

        public static PrimitiveTranscodeNode Float { get; } = new(sizeof(float), static (ref r, ref w) => w.WriteRaw(r.ReadFixedSpan(sizeof(float))));

        public static PrimitiveTranscodeNode FloatToDouble { get; } = new(sizeof(float), static (ref r, ref w) => w.WriteDouble(r.ReadFloat()));

        public static PrimitiveTranscodeNode Double { get; } = new(sizeof(double), static (ref r, ref w) => w.WriteRaw(r.ReadFixedSpan(sizeof(double))));

        public static PrimitiveTranscodeNode Bytes { get; } = new(1, static (ref r, ref w) => w.WriteBytes(r.ReadBytesSpan()));

        public override int MinimumSize => _minimumSize;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch) => _copy(ref reader, ref writer);
    }

    private sealed class CopyFixedNode(int size) : TranscodeNode
    {
        public override int MinimumSize => size;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch) =>
            writer.WriteRaw(reader.ReadFixedSpan(size));
    }

    private sealed class EnumTranscodeNode(EnumSchema writerSchema, EnumSchema readerSchema, int[] map) : TranscodeNode
    {
        public override int MinimumSize => 1;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            var ordinal = reader.ReadEnum();
            if ((uint)ordinal >= (uint)map.Length)
            {
                throw new AvroDataException($"Enum ordinal {ordinal} is out of range for '{writerSchema.FullName}' ({map.Length} symbols).");
            }

            var target = map[ordinal];
            writer.WriteEnum(target >= 0
                ? target
                : throw new AvroDataException($"The symbol '{writerSchema.Symbols[ordinal]}' is not in the reader's enum '{readerSchema.FullName}', which has no default."));
        }
    }

    private sealed class WriterUnionTranscodeNode(TranscodeNode[] branches) : TranscodeNode
    {
        public override int MinimumSize => 1;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            var index = reader.ReadUnionIndex();
            if ((uint)index >= (uint)branches.Length)
            {
                throw new AvroDataException($"Union branch index {index} is out of range ({branches.Length} branches).");
            }

            // Not counted as nesting: a union cannot hold a union, so the arrays, maps and records in it count (#129).
            branches[index].Transcode(ref reader, ref writer, ref state, scratch);
        }
    }

    /// <summary>A non-union writer value read as one branch of a reader union: the branch index, then the value.</summary>
    private sealed class ReaderBranchTranscodeNode(int branch, TranscodeNode value) : TranscodeNode
    {
        public override int MinimumSize => value.MinimumSize;

        public override long ZeroSizeCost => ZeroSizeValues.Add(1, value.ZeroSizeCost);

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            writer.WriteUnionIndex(branch);
            value.Transcode(ref reader, ref writer, ref state, scratch);
        }
    }

    private sealed class ErrorTranscodeNode(string message, int minimumSize) : TranscodeNode
    {
        public override int MinimumSize => minimumSize;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch) =>
            throw new AvroDataException(message);
    }

    private sealed class SkipTranscodeNode(ReaderNode skip) : TranscodeNode
    {
        public override int MinimumSize => skip.MinimumSize;

        public override long ZeroSizeCost => skip.ZeroSizeCost;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch) =>
            skip.Read(ref reader, ref state);
    }

    /// <summary>An array or map: each block is written again with its count, so the reader sees ordinary blocks.</summary>
    private sealed class BlocksTranscodeNode(TranscodeNode items, bool isMap) : TranscodeNode
    {
        public override int MinimumSize => 1;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            EnterNesting(ref state);
            var itemsSoFar = 0;
            long count;
            while ((count = reader.ReadBlockCount(out _)) != 0)
            {
                CheckBlockCount(ref reader, ref state, count, itemsSoFar, (isMap ? 1 : 0) + items.MinimumSize, items.ZeroSizeCost);
                itemsSoFar += (int)count;
                writer.WriteBlockCount(count);
                for (var i = 0L; i < count; i++)
                {
                    if (isMap)
                    {
                        writer.WriteBytes(reader.ReadStringUtf8());
                    }

                    items.Transcode(ref reader, ref writer, ref state, scratch);
                }
            }

            writer.WriteBlockEnd();
            state.Nesting--;
        }
    }

    /// <summary>A record: the writer's fields in the writer's order, written in the reader's order with defaults for the rest.</summary>
    private sealed class RecordTranscodeNode(int readerFieldCount) : TranscodeNode
    {
        public (int Target, TranscodeNode Node)[] Steps { get; set; } = [];

        /// <summary>Gets or sets the encoded default of each reader field the writer lacks; null for the others.</summary>
        public byte[]?[] Defaults { get; set; } = [];

        public bool InOrder { get; set; }

        public int Size { get; set; }

        public override int MinimumSize => Size;

        // The record, the writer's fields, and each byte of the reader's defaults it writes.
        public override long ZeroSizeCost
        {
            get
            {
                if (_zeroSizeCost == 0)
                {
                    // Set while the fields are summed, so a record that holds itself counts once there.
                    _zeroSizeCost = 1;
                    var total = 1L;
                    foreach (var (_, node) in Steps)
                    {
                        total = ZeroSizeValues.Add(total, node.ZeroSizeCost);
                    }

                    foreach (var encoded in Defaults)
                    {
                        total = ZeroSizeValues.Add(total, encoded?.Length ?? 0);
                    }

                    _zeroSizeCost = total;
                }

                return _zeroSizeCost;
            }
        }

        private long _zeroSizeCost;

        public override void Transcode(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            EnterRecord(ref state);
            if (InOrder)
            {
                TranscodeInOrder(ref reader, ref writer, ref state, scratch);
            }
            else
            {
                TranscodeReordered(ref reader, ref writer, ref state, scratch);
            }

            ExitRecord(ref state);
        }

        private void TranscodeInOrder(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            var next = 0;
            foreach (var (target, node) in Steps)
            {
                if (target < 0)
                {
                    node.Transcode(ref reader, ref writer, ref state, scratch);
                    continue;
                }

                for (; next < target; next++)
                {
                    writer.WriteRaw(Defaults[next]);
                }

                node.Transcode(ref reader, ref writer, ref state, scratch);
                next = target + 1;
            }

            for (; next < readerFieldCount; next++)
            {
                writer.WriteRaw(Defaults[next]);
            }
        }

        private void TranscodeReordered(ref AvroReader reader, ref AvroWriter writer, ref ReadState state, ScratchBuffers scratch)
        {
            // Rented rather than allocated: this runs once per record, nested ones included. Only the first
            // readerFieldCount slots are used, and the array is cleared when returned, so it holds no stale buffers.
            var fields = ArrayPool<PooledBufferWriter?>.Shared.Rent(readerFieldCount);
            try
            {
                foreach (var (target, node) in Steps)
                {
                    if (target < 0)
                    {
                        node.Transcode(ref reader, ref writer, ref state, scratch);
                        continue;
                    }

                    var buffer = fields[target] = scratch.Rent();
                    var fieldWriter = new AvroWriter(buffer);
                    node.Transcode(ref reader, ref fieldWriter, ref state, scratch);
                    fieldWriter.Flush();
                }

                for (var i = 0; i < readerFieldCount; i++)
                {
                    writer.WriteRaw(fields[i] is { } buffer ? buffer.WrittenSpan : Defaults[i]);
                }
            }
            finally
            {
                for (var i = 0; i < readerFieldCount; i++)
                {
                    if (fields[i] is { } buffer)
                    {
                        scratch.Return(buffer);
                    }
                }

                ArrayPool<PooledBufferWriter?>.Shared.Return(fields, clearArray: true);
            }
        }
    }
}
