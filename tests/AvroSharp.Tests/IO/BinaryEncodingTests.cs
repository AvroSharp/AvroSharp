using System;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.IO;

namespace AvroSharp.Tests.IO;

/// <summary>The "Binary Encoding" section of the specification, including its examples.</summary>
/// <remarks>Readers are ref structs, so values are read in synchronous helpers and asserted afterwards.</remarks>
public class BinaryEncodingTests
{
    internal delegate void WriteAction(ref AvroWriter writer);

    [Test]
    [Arguments(0L, "00")]
    [Arguments(-1L, "01")]
    [Arguments(1L, "02")]
    [Arguments(-2L, "03")]
    [Arguments(2L, "04")]
    [Arguments(-64L, "7F")]
    [Arguments(64L, "8001")]
    [Arguments(long.MaxValue, "FEFFFFFFFFFFFFFFFF01")]
    [Arguments(long.MinValue, "FFFFFFFFFFFFFFFFFF01")]
    public async Task Long_UsesZigZagVarints(long value, string hex)
    {
        await Assert.That(Encode((ref AvroWriter w) => w.WriteLong(value))).IsEqualTo(hex);
        await Assert.That(ReadLongConsumingAll(hex)).IsEqualTo((value, hex.Length / 2L));
    }

    [Test]
    [Arguments(0, "00")]
    [Arguments(-1, "01")]
    [Arguments(63, "7E")]
    [Arguments(-64, "7F")]
    [Arguments(64, "8001")]
    [Arguments(int.MaxValue, "FEFFFFFF0F")]
    [Arguments(int.MinValue, "FFFFFFFF0F")]
    public async Task Int_UsesZigZagVarints(int value, string hex)
    {
        await Assert.That(Encode((ref AvroWriter w) => w.WriteInt(value))).IsEqualTo(hex);
        await Assert.That(ReadIntConsumingAll(hex)).IsEqualTo((value, hex.Length / 2L));
    }

    [Test]
    public async Task SpecificationExamples_AreReproduced()
    {
        // string "foo"
        await Assert.That(Encode((ref AvroWriter w) => w.WriteString("foo"))).IsEqualTo("06666F6F");

        // record {a: long = 27, b: string = "foo"}
        await Assert.That(Encode((ref AvroWriter w) =>
        {
            w.WriteLong(27);
            w.WriteString("foo");
        })).IsEqualTo("3606666F6F");

        // array of longs [3, 27]
        await Assert.That(Encode((ref AvroWriter w) =>
        {
            w.WriteBlockCount(2);
            w.WriteLong(3);
            w.WriteLong(27);
            w.WriteBlockEnd();
        })).IsEqualTo("04063600");

        // union ["null","string"]: null, then "a"
        await Assert.That(Encode((ref AvroWriter w) => w.WriteUnionIndex(0))).IsEqualTo("00");
        await Assert.That(Encode((ref AvroWriter w) =>
        {
            w.WriteUnionIndex(1);
            w.WriteString("a");
        })).IsEqualTo("020261");
    }

    [Test]
    public async Task Primitives_RoundTrip()
    {
        var bytes = EncodeBytes((ref AvroWriter w) =>
        {
            w.WriteNull();
            w.WriteBoolean(true);
            w.WriteBoolean(false);
            w.WriteFloat(-1.5f);
            w.WriteFloat(float.NaN);
            w.WriteDouble(Math.PI);
            w.WriteDouble(double.NegativeInfinity);
            w.WriteBytes([1, 2, 3]);
            w.WriteBytes([]);
            w.WriteString("Grüße 日本 🎉");
            w.WriteString(string.Empty);
            w.WriteStringUtf8("utf8"u8);
            w.WriteFixed([9, 8, 7, 6]);
            w.WriteEnum(3);
        });

        var values = ReadPrimitives(bytes);
        await Assert.That(values).IsEquivalentTo(new object[]
        {
            true, false, -1.5f, true, Math.PI, double.NegativeInfinity, "010203", 0, "Grüße 日本 🎉", string.Empty,
            "utf8", "09080706", 3, true, (long)bytes.Length,
        });
    }

    [Test]
    public async Task FloatAndDouble_AreLittleEndianBitPatterns()
    {
        await Assert.That(Encode((ref AvroWriter w) => w.WriteFloat(1.0f))).IsEqualTo("0000803F");
        await Assert.That(Encode((ref AvroWriter w) => w.WriteDouble(1.0))).IsEqualTo("000000000000F03F");
    }

    [Test]
    public async Task BulkDoublesAndFloats_MatchItemByItemEncoding()
    {
        var doubles = Enumerable.Range(0, 100).Select(i => (i * 1.25) - 40).ToArray();
        var floats = doubles.Select(d => (float)d).ToArray();

        var bulk = Encode((ref AvroWriter w) =>
        {
            w.WriteDoubles(doubles);
            w.WriteFloats(floats);
        });
        var single = Encode((ref AvroWriter w) =>
        {
            foreach (var d in doubles)
            {
                w.WriteDouble(d);
            }

            foreach (var f in floats)
            {
                w.WriteFloat(f);
            }
        });
        await Assert.That(bulk).IsEqualTo(single);

        var bytes = Convert.FromHexString(bulk);
        var (contiguousDoubles, contiguousFloats) = ReadBulk(new AvroReader(bytes), doubles.Length);
        await Assert.That(contiguousDoubles).IsEquivalentTo(doubles);
        await Assert.That(contiguousFloats).IsEquivalentTo(floats);

        // A byte-by-byte sequence takes the per-item path.
        var (segmentedDoubles, segmentedFloats) = ReadBulk(new AvroReader(Segments.ByteByByte(bytes)), doubles.Length);
        await Assert.That(segmentedDoubles).IsEquivalentTo(doubles);
        await Assert.That(segmentedFloats).IsEquivalentTo(floats);
    }

    [Test]
    public async Task ReadBytesSpan_DoesNotCopyContiguousInput()
    {
        var bytes = EncodeBytes((ref AvroWriter w) => w.WriteBytes([1, 2, 3, 4]));
        var firstByteAfterMutation = ReadFirstByteAfterMutating(bytes);
        await Assert.That(firstByteAfterMutation).IsEqualTo((byte)42);
    }

    [Test]
    public async Task BlockCount_WithByteSize_AllowsSkipping()
    {
        // A negative count is followed by the block's byte size.
        var bytes = EncodeBytes((ref AvroWriter w) =>
        {
            w.WriteLong(-3);
            w.WriteLong(3);
            w.WriteLong(3);
            w.WriteLong(27);
            w.WriteLong(5);
            w.WriteBlockEnd();
            w.WriteInt(99);
        });

        var reader = new AvroReader(bytes);
        var count = reader.ReadBlockCount(out var size);
        reader.Skip(size);
        var end = reader.ReadBlockCount(out var endSize);
        var marker = reader.ReadInt();

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(size).IsEqualTo(3);
        await Assert.That(end).IsEqualTo(0);
        await Assert.That(endSize).IsEqualTo(-1);
        await Assert.That(marker).IsEqualTo(99);
    }

    [Test]
    public async Task SkipHelpers_ConsumeWholeValues()
    {
        var bytes = EncodeBytes((ref AvroWriter w) =>
        {
            w.WriteLong(long.MaxValue);
            w.WriteString("skip me");
            w.WriteFixed([1, 2, 3]);
            w.WriteInt(7);
        });

        var reader = new AvroReader(bytes);
        reader.SkipVarint();
        reader.SkipBytes();
        reader.Skip(3);
        await Assert.That(reader.ReadInt()).IsEqualTo(7);
    }

    [Test]
    public async Task EverySplitPoint_ReadsTheSameValues()
    {
        var bytes = EncodeBytes(WriteMixture);
        var expected = ReadMixture(new AvroReader(bytes));
        for (var cut = 0; cut <= bytes.Length; cut++)
        {
            await Assert.That(ReadMixture(new AvroReader(Segments.Split(bytes, cut)))).IsEquivalentTo(expected);
        }

        await Assert.That(ReadMixture(new AvroReader(Segments.ByteByByte(bytes)))).IsEquivalentTo(expected);
        await Assert.That(expected).IsEquivalentTo(MixtureValues);
    }

    [Test]
    public async Task Writer_ToASmallSpan_ReportsTooSmall()
    {
        var ex = Assert.Throws<AvroException>(() =>
        {
            Span<byte> small = stackalloc byte[4];
            var writer = new AvroWriter(small);
            writer.WriteString("longer than four bytes");
        });
        await Assert.That(ex.Message).Contains("too small");
    }

    [Test]
    public async Task Writer_ToASpan_ReportsBytesWritten()
    {
        var destination = new byte[16];
        var writer = new AvroWriter(destination.AsSpan());
        writer.WriteLong(64);
        writer.WriteString("a");
        var written = writer.BytesWritten;
        await Assert.That(written).IsEqualTo(4L);
        await Assert.That(Convert.ToHexString(destination, 0, 4)).IsEqualTo("80010261");
    }

    [Test]
    public async Task Writer_SplitsLargeValuesAcrossSmallBuffers()
    {
        // An IBufferWriter that hands out small spans forces every chunking path.
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var output = new TinyBufferWriter(chunkSize: 7);
        var writer = new AvroWriter(output);
        writer.WriteBytes(payload);
        writer.WriteString(new string('x', 3000));
        writer.WriteLong(long.MinValue);
        var pendingBeforeFlush = writer.BytesPending;
        writer.Flush();
        var pendingAfterFlush = writer.BytesPending;
        var written = writer.BytesWritten;

        var reader = new AvroReader(output.WrittenSpan);
        var readPayload = reader.ReadBytes();
        var readString = reader.ReadString();
        var readLong = reader.ReadLong();

        await Assert.That(pendingBeforeFlush).IsGreaterThan(0);
        await Assert.That(pendingAfterFlush).IsEqualTo(0);
        await Assert.That(written).IsEqualTo(output.WrittenCount);
        await Assert.That(readPayload).IsEquivalentTo(payload);
        await Assert.That(readString).IsEqualTo(new string('x', 3000));
        await Assert.That(readLong).IsEqualTo(long.MinValue);
    }

    [Test]
    public async Task Writer_RejectsNonPositiveBlockCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var writer = new AvroWriter(new ArrayBufferWriter<byte>());
            writer.WriteBlockCount(0);
        });
        await Task.CompletedTask;
    }

    internal static readonly object[] MixtureValues =
    [
        long.MinValue, int.MaxValue, true, BitConverter.DoubleToInt64Bits(-0.0), 3.5f, "multi-segment ✓",
        "FF007F", "0102030405", 2L, 300L, -300L, 0L, true,
    ];

    internal static void WriteMixture(ref AvroWriter w)
    {
        w.WriteLong(long.MinValue);
        w.WriteInt(int.MaxValue);
        w.WriteBoolean(true);
        w.WriteDouble(-0.0);
        w.WriteFloat(3.5f);
        w.WriteString("multi-segment ✓");
        w.WriteBytes([0xFF, 0x00, 0x7F]);
        w.WriteFixed([1, 2, 3, 4, 5]);
        w.WriteBlockCount(2);
        w.WriteLong(300);
        w.WriteLong(-300);
        w.WriteBlockEnd();
    }

    internal static object[] ReadMixture(AvroReader r) =>
    [
        r.ReadLong(), r.ReadInt(), r.ReadBoolean(), BitConverter.DoubleToInt64Bits(r.ReadDouble()), r.ReadFloat(), r.ReadString(),
        Convert.ToHexString(r.ReadBytes()), Convert.ToHexString(r.ReadFixedSpan(5).ToArray()), r.ReadBlockCount(out _),
        r.ReadLong(), r.ReadLong(), r.ReadBlockCount(out _), r.IsAtEnd,
    ];

    internal static byte[] EncodeBytes(WriteAction write)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        write(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    internal static string Encode(WriteAction write) => Convert.ToHexString(EncodeBytes(write));

    /// <summary>Returns the value and how many bytes it consumed, so a short read cannot pass unnoticed.</summary>
    private static (long Value, long Consumed) ReadLongConsumingAll(string hex)
    {
        var reader = new AvroReader(Convert.FromHexString(hex));
        return (reader.ReadLong(), reader.BytesConsumed);
    }

    private static (int Value, long Consumed) ReadIntConsumingAll(string hex)
    {
        var reader = new AvroReader(Convert.FromHexString(hex));
        return (reader.ReadInt(), reader.BytesConsumed);
    }

    private static object[] ReadPrimitives(byte[] bytes)
    {
        var r = new AvroReader(bytes);
        r.ReadNull();
        return
        [
            r.ReadBoolean(), r.ReadBoolean(), r.ReadFloat(), float.IsNaN(r.ReadFloat()), r.ReadDouble(), r.ReadDouble(),
            Convert.ToHexString(r.ReadBytes()), r.ReadBytesSpan().Length, r.ReadString(), r.ReadString(),
            Encoding.UTF8.GetString(r.ReadStringUtf8().ToArray()), Convert.ToHexString(r.ReadFixedSpan(4).ToArray()),
            r.ReadEnum(), r.IsAtEnd, r.BytesConsumed,
        ];
    }

    private static (double[] Doubles, float[] Floats) ReadBulk(AvroReader reader, int count)
    {
        var doubles = new double[count];
        var floats = new float[count];
        reader.ReadDoubles(doubles);
        reader.ReadFloats(floats);
        return (doubles, floats);
    }

    private static byte ReadFirstByteAfterMutating(byte[] bytes)
    {
        var reader = new AvroReader(bytes);
        var span = reader.ReadBytesSpan();
        bytes[1] = 42;
        return span[0];
    }

    private sealed class TinyBufferWriter(int chunkSize) : IBufferWriter<byte>
    {
        private byte[] _data = new byte[64];
        private int _written;

        public int WrittenCount => _written;

        public ReadOnlySpan<byte> WrittenSpan => _data.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint).Span;

        private Memory<byte> Reserve(int sizeHint)
        {
            var size = Math.Max(sizeHint, chunkSize);
            if (_data.Length - _written < size)
            {
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _written + size));
            }

            // Hand out exactly the requested size (or the chunk size), never more.
            return _data.AsMemory(_written, size);
        }
    }
}
