using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.IO;

namespace AvroSharp.Tests.IO;

/// <summary>
/// ReadLongs/ReadInts must return exactly what one-at-a-time reads return, for any mix of one-byte and longer values,
/// any count, and any input layout. The vector path runs when hardware acceleration is available; CI also runs the
/// suite with hardware intrinsics disabled, which exercises the scalar path.
/// </summary>
public class BulkReadTests
{
    public static IEnumerable<Func<long[]>> Datasets()
    {
        var random = new Random(99);
        yield return () => [];
        yield return () => [1];
        yield return () => Enumerable.Range(-8, 16).Select(i => (long)i).ToArray();
        yield return () => Enumerable.Range(-100, 200).Select(i => (long)(i % 60)).ToArray();

        // Runs of small values broken by larger ones at every position.
        for (var breakAt = 0; breakAt < 20; breakAt++)
        {
            var copy = Enumerable.Range(0, 40).Select(i => i == breakAt || i == breakAt + 17 ? 1_000_000L + i : i % 30L).ToArray();
            yield return () => copy;
        }

        // Random mixtures with different densities of multi-byte values.
        foreach (var density in new[] { 0.0, 0.05, 0.3, 1.0 })
        {
            var copy = Enumerable.Range(0, 300).Select(_ => random.NextDouble() < density ? random.NextInt64(long.MinValue, long.MaxValue) : random.Next(-64, 64)).ToArray();
            yield return () => copy;
        }
    }

    [Test]
    [MethodDataSource(nameof(Datasets))]
    public async Task ReadLongs_MatchesOneAtATimeReads(long[] values)
    {
        var bytes = Encode(values);
        var expected = ReadOneAtATime(bytes, values.Length);

        await Assert.That(ReadBulk(new AvroReader(bytes), values.Length)).IsEquivalentTo(expected);
        await Assert.That(ReadBulk(new AvroReader(Segments.ByteByByte(bytes)), values.Length)).IsEquivalentTo(expected);
        await Assert.That(ReadBulk(new AvroReader(Segments.Split(bytes, bytes.Length / 2)), values.Length)).IsEquivalentTo(expected);
        await Assert.That(expected).IsEquivalentTo(values);
    }

    [Test]
    [MethodDataSource(nameof(Datasets))]
    public async Task ReadInts_MatchesOneAtATimeReads(long[] values)
    {
        var ints = values.Select(v => (int)v).ToArray();
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        foreach (var value in ints)
        {
            writer.WriteInt(value);
        }

        writer.Flush();
        var bytes = output.WrittenSpan.ToArray();

        await Assert.That(ReadIntsBulk(new AvroReader(bytes), ints.Length)).IsEquivalentTo(ints);
        await Assert.That(ReadIntsBulk(new AvroReader(Segments.ByteByByte(bytes)), ints.Length)).IsEquivalentTo(ints);
    }

    [Test]
    public async Task ReadLongs_ConsumesExactlyTheValuesRead()
    {
        var bytes = Encode([.. Enumerable.Range(0, 20).Select(i => (long)i), 123456789L, 5L]);
        var (consumed, next) = ReadPrefixThenNext(bytes, 20);
        await Assert.That(consumed).IsEqualTo(20L);
        await Assert.That(next).IsEqualTo(123456789L);
    }

    [Test]
    public async Task ReadLongs_TruncatedInput_Throws()
    {
        var bytes = Encode(Enumerable.Range(0, 20).Select(i => (long)i).ToArray());
        Assert.Throws<AvroDataException>(() => new AvroReader(bytes).ReadLongs(new long[21]));
        await Task.CompletedTask;
    }

    private static byte[] Encode(long[] values)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        foreach (var value in values)
        {
            writer.WriteLong(value);
        }

        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static long[] ReadOneAtATime(byte[] bytes, int count)
    {
        var reader = new AvroReader(bytes);
        var result = new long[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = reader.ReadLong();
        }

        return result;
    }

    private static long[] ReadBulk(AvroReader reader, int count)
    {
        var result = new long[count];
        reader.ReadLongs(result);
        return reader.IsAtEnd ? result : throw new InvalidOperationException("Bytes left over.");
    }

    private static int[] ReadIntsBulk(AvroReader reader, int count)
    {
        var result = new int[count];
        reader.ReadInts(result);
        return reader.IsAtEnd ? result : throw new InvalidOperationException("Bytes left over.");
    }

    private static (long Consumed, long Next) ReadPrefixThenNext(byte[] bytes, int count)
    {
        var reader = new AvroReader(bytes);
        reader.ReadLongs(new long[count]);
        var consumed = reader.BytesConsumed;
        return (consumed, reader.ReadLong());
    }
}
