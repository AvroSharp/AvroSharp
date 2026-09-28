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
        await Assert.That(Encode((ref w) => w.WriteLong(value))).IsEqualTo(hex);
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
        await Assert.That(Encode((ref w) => w.WriteInt(value))).IsEqualTo(hex);
        await Assert.That(ReadIntConsumingAll(hex)).IsEqualTo((value, hex.Length / 2L));
    }

    [Test]
    public async Task SpecificationExamples_AreReproduced()
    {
        // string "foo"
        await Assert.That(Encode((ref w) => w.WriteString("foo"))).IsEqualTo("06666F6F");

        // record {a: long = 27, b: string = "foo"}
        await Assert.That(Encode((ref w) =>
        {
            w.WriteLong(27);
            w.WriteString("foo");
        })).IsEqualTo("3606666F6F");

        // array of longs [3, 27]
        await Assert.That(Encode((ref w) =>
        {
            w.WriteBlockCount(2);
            w.WriteLong(3);
            w.WriteLong(27);
            w.WriteBlockEnd();
        })).IsEqualTo("04063600");

        // union ["null","string"]: null, then "a"
        await Assert.That(Encode((ref w) => w.WriteUnionIndex(0))).IsEqualTo("00");
        await Assert.That(Encode((ref w) =>
        {
            w.WriteUnionIndex(1);
            w.WriteString("a");
        })).IsEqualTo("020261");
    }

    [Test]
    public async Task Primitives_RoundTrip()
    {
        var bytes = EncodeBytes((ref w) =>
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
        await Assert.That(Encode((ref w) => w.WriteFloat(1.0f))).IsEqualTo("0000803F");
        await Assert.That(Encode((ref w) => w.WriteDouble(1.0))).IsEqualTo("000000000000F03F");
    }

    [Test]
    public async Task BulkDoublesAndFloats_MatchItemByItemEncoding()
    {
        var doubles = Enumerable.Range(0, 100).Select(i => (i * 1.25) - 40).ToArray();
        var floats = doubles.Select(d => (float)d).ToArray();

        var bulk = Encode((ref w) =>
        {
            w.WriteDoubles(doubles);
            w.WriteFloats(floats);
        });
        var single = Encode((ref w) =>
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
    public async Task BulkBooleans_MatchItemByItemEncoding()
    {
        var values = Enumerable.Range(0, 100).Select(i => i % 3 == 0).ToArray();
        var bulk = Encode((ref w) => w.WriteBooleans(values));
        var single = Encode((ref w) =>
        {
            foreach (var v in values)
            {
                w.WriteBoolean(v);
            }
        });
        await Assert.That(bulk).IsEqualTo(single);

        var bytes = Convert.FromHexString(bulk);
        await Assert.That(ReadBooleans(new AvroReader(bytes), values.Length)).IsEquivalentTo(values);
        await Assert.That(ReadBooleans(new AvroReader(Segments.ByteByByte(bytes)), values.Length)).IsEquivalentTo(values);
    }

    [Test]
    public async Task BulkBooleans_RejectInvalidBytes_AndTruncatedInput()
    {
        byte[] bytes = [0x07, 0x01, 0x00, 0x01, 0xFF];
        var ex = Assert.Throws<AvroDataException>(() => ReadBooleans(new AvroReader(bytes.AsSpan(1)), 4));
        await Assert.That(ex.Message).IsEqualTo("Invalid boolean byte 0xFF at offset 3; expected 0 or 1.");
        ex = Assert.Throws<AvroDataException>(() => ReadBooleans(new AvroReader(Segments.ByteByByte(bytes.AsSpan(1).ToArray())), 4));
        await Assert.That(ex.Message).IsEqualTo("Invalid boolean byte 0xFF at offset 3; expected 0 or 1.");
        ex = Assert.Throws<AvroDataException>(() => ReadBooleans(new AvroReader(bytes.AsSpan(1, 3)), 4));
        await Assert.That(ex.Message).Contains("boolean value needs 4 byte(s)");

        // The offset counts bytes read before the bulk call.
        var offset = Assert.Throws<AvroDataException>(() => ReadBooleansAfterInt(bytes, 4));
        await Assert.That(offset.Message).IsEqualTo("Invalid boolean byte 0xFF at offset 4; expected 0 or 1.");
    }

    [Test]
    public async Task BulkIntsAndLongs_MatchItemByItemEncoding()
    {
        int[] ints = [0, 1, -1, 63, -64, 64, int.MaxValue, int.MinValue];
        long[] longs = [0, 1, -1, 1L << 40, long.MaxValue, long.MinValue];
        var bulk = Encode((ref w) =>
        {
            w.WriteInts(ints);
            w.WriteLongs(longs);
        });
        var single = Encode((ref w) =>
        {
            foreach (var i in ints)
            {
                w.WriteInt(i);
            }

            foreach (var l in longs)
            {
                w.WriteLong(l);
            }
        });
        await Assert.That(bulk).IsEqualTo(single);
    }

    [Test]
    public async Task SpanWriterForTryWrites_ReportsOverflowInsteadOfThrowing()
    {
        // Every write path runs out of room at some point: varints near the end, raw bytes, strings.
        foreach (var size in new[] { 0, 1, 5, 9, 12, 30 })
        {
            var (overflowed, written) = TryWriteMixture(new byte[size]);
            await Assert.That(overflowed).IsTrue();
            await Assert.That(written).IsEqualTo(0);
        }

        var (fitOverflowed, fitWritten) = TryWriteMixture(new byte[128]);
        await Assert.That(fitOverflowed).IsFalse();
        await Assert.That(fitWritten).IsEqualTo(Convert.FromHexString(Encode(WriteMixtureValues)).Length);
    }

    private static (bool Overflowed, int Written) TryWriteMixture(byte[] destination)
    {
        var writer = AvroSharp.Serialization.AvroGeneratedCode.BeginTryWrite(destination);
        WriteMixtureValues(ref writer);
        return (!AvroSharp.Serialization.AvroGeneratedCode.EndTryWrite(ref writer, out var written), written);
    }

    private static void WriteMixtureValues(ref AvroWriter w)
    {
        w.WriteLong(long.MaxValue);
        w.WriteString("a string that is longer than the small buffers");
        w.WriteBytes([1, 2, 3]);
        w.WriteDouble(1.5);
    }

    [Test]
    public async Task WriteBooleans_WritesOtherBytesAsTrue()
    {
        // Only unsafe code can make such a bool; it is written as WriteBoolean writes it.
        var raw = new byte[] { 0, 1, 2 };
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, bool>(raw).ToArray();
        await Assert.That(Encode((ref w) => w.WriteBooleans(values))).IsEqualTo("000101");
    }

    [Test]
    public async Task ReadBytesSpan_DoesNotCopyContiguousInput()
    {
        var bytes = EncodeBytes((ref w) => w.WriteBytes([1, 2, 3, 4]));
        var firstByteAfterMutation = ReadFirstByteAfterMutating(bytes);
        await Assert.That(firstByteAfterMutation).IsEqualTo((byte)42);
    }

    [Test]
    public async Task BlockCount_WithByteSize_AllowsSkipping()
    {
        // A negative count is followed by the block's byte size.
        var bytes = EncodeBytes((ref w) =>
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
        var bytes = EncodeBytes((ref w) =>
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
    public async Task Strings_EncodeTheSame_InOnePassAndInTwo()
    {
        // Lengths around the points where the length prefix grows (1 to 2 bytes at 64 UTF-8 bytes, 2 to 3 at 8192),
        // with 1- to 4-byte characters and a lone surrogate. A large buffer takes the one-pass path, where the prefix
        // is sized for 3 bytes per char and the bytes may move down; a buffer of exactly the encoded size, and a
        // writer that hands out tiny spans, take the two-pass path.
        var pieces = new[] { "a", "é", "日", "🎉", "\uD800" };
        var lengths = new[] { 0, 1, 20, 21, 22, 31, 32, 42, 63, 64, 65, 200, 2730, 2731, 4096, 8191, 8192, 9000 };
        var failures = 0;
        foreach (var piece in pieces)
        {
            foreach (var length in lengths)
            {
                var value = string.Concat(Enumerable.Repeat(piece, length));
                var expected = Utf8Reference(value);

                var large = new byte[expected.Length + (value.Length * 3) + 16];
                var writer = new AvroWriter(large.AsSpan());
                writer.WriteString(value);
                var onePass = large.AsSpan(0, (int)writer.BytesWritten).ToArray();

                var exact = new byte[expected.Length];
                var exactWriter = new AvroWriter(exact.AsSpan());
                exactWriter.WriteString(value);

                var tiny = new TinyBufferWriter(chunkSize: 7);
                var tinyWriter = new AvroWriter(tiny);
                tinyWriter.WriteString(value);
                tinyWriter.Flush();

                var pooled = new ArrayBufferWriter<byte>();
                var pooledWriter = new AvroWriter(pooled);
                pooledWriter.WriteLong(5);
                pooledWriter.WriteString(value);
                pooledWriter.WriteLong(-5);
                pooledWriter.Flush();

                var reader = new AvroReader(pooled.WrittenSpan);
                var roundTrip = reader.ReadLong() == 5 && string.Equals(reader.ReadString(), Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value)), StringComparison.Ordinal) && reader.ReadLong() == -5;

                if (!onePass.SequenceEqual(expected) || !exact.SequenceEqual(expected) || !tiny.WrittenSpan.SequenceEqual(expected) || !roundTrip)
                {
                    failures++;
                }
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    private static byte[] Utf8Reference(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var prefix = new System.Collections.Generic.List<byte>();
        var zigZag = (ulong)utf8.Length << 1;
        while (zigZag >= 0x80)
        {
            prefix.Add((byte)(zigZag | 0x80));
            zigZag >>= 7;
        }

        prefix.Add((byte)zigZag);
        return [.. prefix, .. utf8];
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

    private static bool[] ReadBooleans(AvroReader reader, int count)
    {
        var values = new bool[count];
        reader.ReadBooleans(values);
        return values;
    }

    private static bool[] ReadBooleansAfterInt(byte[] bytes, int count)
    {
        var reader = new AvroReader(bytes);
        _ = reader.ReadInt();
        var values = new bool[count];
        reader.ReadBooleans(values);
        return values;
    }

    private static byte ReadFirstByteAfterMutating(byte[] bytes)
    {
        var reader = new AvroReader(bytes);
        var span = reader.ReadBytesSpan();
        bytes[1] = 42;
        return span[0];
    }
}
