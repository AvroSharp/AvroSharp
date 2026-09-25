using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.IO;

namespace AvroSharp.Tests.IO;

/// <summary>
/// The reader and writer have several varint paths (one byte, one 8-byte word, byte by byte, exact size). Each
/// length from 1 to 10 bytes is run through every path and compared with an independent reference implementation.
/// </summary>
public class VarintPathTests
{
    /// <summary>Unsigned values whose varint encoding is exactly 1 to 10 bytes long, including both edges of each length.</summary>
    public static IEnumerable<Func<ulong[]>> ValuesByLength()
    {
        var random = new Random(1234);
        for (var length = 1; length <= 10; length++)
        {
            var low = length == 1 ? 0UL : 1UL << (7 * (length - 1));
            var high = length == 10 ? ulong.MaxValue : (1UL << (7 * length)) - 1;
            var values = new List<ulong> { low, high };
            for (var i = 0; i < 20; i++)
            {
                var span = high - low;
                values.Add(low + (span == ulong.MaxValue ? (ulong)random.NextInt64() : (ulong)(random.NextDouble() * span)));
            }

            var copy = values.ToArray();
            yield return () => copy;
        }
    }

    [Test]
    [MethodDataSource(nameof(ValuesByLength))]
    public async Task Long_EveryPath_MatchesTheReferenceEncoding(ulong[] zigZagValues)
    {
        foreach (var zigZag in zigZagValues)
        {
            var value = (long)(zigZag >> 1) ^ -(long)(zigZag & 1);
            var reference = ReferenceEncode(zigZag);

            // Writer: IBufferWriter (fast paths) and an exactly sized span (exact path).
            await Assert.That(Convert.ToHexString(WriteToBufferWriter(value))).IsEqualTo(Convert.ToHexString(reference));
            await Assert.That(Convert.ToHexString(WriteToExactSpan(value, reference.Length))).IsEqualTo(Convert.ToHexString(reference));

            // Reader: followed by padding (8-byte word path), alone (end of input), and one byte per segment.
            var padded = reference.Concat(new byte[16]).ToArray();
            await Assert.That(ReadLong(padded)).IsEqualTo((value, (long)reference.Length));
            await Assert.That(ReadLong(reference)).IsEqualTo((value, (long)reference.Length));
            await Assert.That(ReadLongSegmented(reference)).IsEqualTo(value);
        }
    }

    [Test]
    [MethodDataSource(nameof(ValuesByLength))]
    public async Task Int_EveryPath_MatchesTheReferenceEncoding(ulong[] zigZagValues)
    {
        foreach (var zigZag in zigZagValues.Where(v => v <= uint.MaxValue))
        {
            var value = (int)((uint)zigZag >> 1) ^ -(int)((uint)zigZag & 1);
            var reference = ReferenceEncode(zigZag);
            var padded = reference.Concat(new byte[16]).ToArray();

            var output = new ArrayBufferWriter<byte>();
            var writer = new AvroWriter(output);
            writer.WriteInt(value);
            writer.Flush();

            await Assert.That(Convert.ToHexString(output.WrittenSpan.ToArray())).IsEqualTo(Convert.ToHexString(reference));
            await Assert.That(ReadInt(padded)).IsEqualTo((value, (long)reference.Length));
            await Assert.That(ReadInt(reference)).IsEqualTo((value, (long)reference.Length));
        }
    }

    [Test]
    [Arguments("808080808000")]
    [Arguments("FFFFFFFFFFFF7F")]
    [Arguments("FFFFFFFFFFFFFFFF")]
    public async Task OverlongInt_IsRejected_WithAndWithoutPadding(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var padded = bytes.Concat(new byte[16]).ToArray();
        Assert.Throws<AvroDataException>(() => new AvroReader(bytes).ReadInt());
        var ex = Assert.Throws<AvroDataException>(() => new AvroReader(padded).ReadInt());
        await Assert.That(ex.Message).Contains("Invalid int encoding");
    }

    [Test]
    public async Task OverlongLong_IsRejected_WithAndWithoutPadding()
    {
        var bytes = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFF01");
        var padded = bytes.Concat(new byte[16]).ToArray();
        Assert.Throws<AvroDataException>(() => new AvroReader(bytes).ReadLong());
        var ex = Assert.Throws<AvroDataException>(() => new AvroReader(padded).ReadLong());
        await Assert.That(ex.Message).Contains("Invalid long encoding");
    }

    [Test]
    public async Task FifthIntByte_HighBitsAreDropped_OnTheWordPathToo()
    {
        var padded = Convert.FromHexString("FFFFFFFF7F0000000000");
        await Assert.That(ReadInt(padded)).IsEqualTo((int.MinValue, 5L));
    }

    [Test]
    public async Task SpanDestination_CanBeFilledToTheLastByte()
    {
        // Each value written into a span of exactly its encoded size.
        await Assert.That(Convert.ToHexString(WriteToExactSpan(0, 1))).IsEqualTo("00");
        await Assert.That(Convert.ToHexString(WriteToExactSpan(64, 2))).IsEqualTo("8001");
        await Assert.That(Convert.ToHexString(WriteToExactSpan(long.MinValue, 10))).IsEqualTo("FFFFFFFFFFFFFFFFFF01");

        var exact = new byte[3];
        var writer = new AvroWriter(exact.AsSpan());
        writer.WriteLong(1);
        writer.WriteInt(-64);
        writer.WriteBoolean(true);
        var written = writer.BytesWritten;
        await Assert.That(written).IsEqualTo(3L);
        await Assert.That(Convert.ToHexString(exact)).IsEqualTo("027F01");
    }

    [Test]
    [MethodDataSource(nameof(ValuesByLength))]
    public async Task Long_AcrossBufferBoundaries_MatchesTheReferenceEncoding(ulong[] zigZagValues)
    {
        // The writer requests at least 256 bytes per buffer; writing the values many times fills buffers up to every
        // possible remainder below 10 bytes, where the exact-size path takes over.
        var output = new TinyBufferWriter(chunkSize: 1);
        var writer = new AvroWriter(output);
        var expected = new List<byte>();
        foreach (var zigZag in Enumerable.Repeat(zigZagValues, 60).SelectMany(v => v))
        {
            writer.WriteLong((long)(zigZag >> 1) ^ -(long)(zigZag & 1));
            expected.AddRange(ReferenceEncode(zigZag));
        }

        writer.Flush();
        await Assert.That(Convert.ToHexString(output.WrittenSpan.ToArray())).IsEqualTo(Convert.ToHexString([.. expected]));
    }

    [Test]
    public async Task SpanDestination_OneByteShort_ThrowsTooSmall()
    {
        var ex = Assert.Throws<AvroException>(() =>
        {
            var writer = new AvroWriter(new byte[1].AsSpan());
            writer.WriteLong(64);
        });
        await Assert.That(ex.Message).Contains("too small");
    }

    private static byte[] ReferenceEncode(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return [.. bytes];
    }

    private static byte[] WriteToBufferWriter(long value)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        writer.WriteLong(value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static byte[] WriteToExactSpan(long value, int size)
    {
        var destination = new byte[size];
        var writer = new AvroWriter(destination.AsSpan());
        writer.WriteLong(value);
        return writer.BytesWritten == size ? destination : throw new InvalidOperationException($"Wrote {writer.BytesWritten} bytes, expected {size}.");
    }

    private static (long Value, long Consumed) ReadLong(byte[] bytes)
    {
        var reader = new AvroReader(bytes);
        return (reader.ReadLong(), reader.BytesConsumed);
    }

    private static (int Value, long Consumed) ReadInt(byte[] bytes)
    {
        var reader = new AvroReader(bytes);
        return (reader.ReadInt(), reader.BytesConsumed);
    }

    private static long ReadLongSegmented(byte[] bytes)
    {
        var reader = new AvroReader(Segments.ByteByByte(bytes));
        return reader.ReadLong();
    }
}
