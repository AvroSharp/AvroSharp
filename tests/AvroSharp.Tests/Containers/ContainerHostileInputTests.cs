using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.Containers;
using AvroSharp.IO;

namespace AvroSharp.Tests.Containers;

/// <summary>Malformed container files must fail with <see cref="AvroDataException"/>, without large allocations.</summary>
public class ContainerHostileInputTests
{
    private const int SyncSize = 16;

    [Test]
    [Arguments("")]
    [Arguments("Obj")]
    [Arguments("Obj\u0002")]
    [Arguments("PK\u0003\u0004")]
    public async Task DataThatIsNotAContainerFile_IsRejected(string start)
    {
        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(Encoding.ASCII.GetBytes(start))));

        await Assert.That(ex.Message).Contains("not an Avro object container file");
    }

    [Test]
    public async Task ATruncatedHeader_IsRejected()
    {
        var header = Header();
        for (var length = 4; length < header.Length; length += 7)
        {
            var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(header, 0, length)));
            await Assert.That(ex.Message).Contains("ends inside");
        }
    }

    [Test]
    public async Task AHeaderWithoutASchema_IsRejected()
    {
        var bytes = Build((ref w) =>
        {
            w.WriteRaw("Obj\u0001"u8);
            w.WriteBlockCount(1);
            w.WriteString("avro.codec");
            w.WriteString("null");
            w.WriteBlockEnd();
            w.WriteRaw(new byte[SyncSize]);
        });

        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));

        await Assert.That(ex.Message).Contains("avro.schema");
    }

    [Test]
    public async Task AnInvalidSchema_IsRejected()
    {
        var bytes = Build((ref w) =>
        {
            w.WriteRaw("Obj\u0001"u8);
            w.WriteBlockCount(1);
            w.WriteString("avro.schema");
            w.WriteString("""{"type":"nope"}""");
            w.WriteBlockEnd();
            w.WriteRaw(new byte[SyncSize]);
        });

        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));

        await Assert.That(ex.Message).Contains("schema is invalid");
    }

    [Test]
    public async Task AHugeMetadataValue_IsRejectedWithoutAllocating()
    {
        var bytes = Build((ref w) =>
        {
            w.WriteRaw("Obj\u0001"u8);
            w.WriteBlockCount(1);
            w.WriteString("big");
            w.WriteLong(int.MaxValue);
        });

#if NET
        var before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));
        await Assert.That(ex.Message).Contains("declares 2147483647 bytes");
#if NET
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsLessThan(1024 * 1024);
#endif
    }

    [Test]
    public async Task ASyncMarkerMismatch_IsRejected()
    {
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), AvroFileWriterOptions.Default);
        bytes[^1] ^= 0xFF;

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(ex.Message).Contains("sync marker");
    }

    [Test]
    public async Task ATruncatedBlock_IsRejected()
    {
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), AvroFileWriterOptions.Default);
        var headerLength = ContainerFileTests.WriteFile([], AvroFileWriterOptions.Default).Length;
        for (var length = headerLength + 1; length < bytes.Length; length += 5)
        {
            using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes, 0, length));
            var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());
            await Assert.That(ex.Message).Contains("ends inside");
        }
    }

    [Test]
    [Arguments(-1L, 0L, "declares -1 objects")]
    [Arguments(1L, -1L, "in -1 bytes")]
    [Arguments(1_000_000L, 1L, "declares 1000000 objects in 1 bytes")]
    [Arguments(0L, 1L, "declares no objects")]
    public async Task ImpossibleBlockCountsAndSizes_AreRejected(long count, long size, string message)
    {
        var bytes = WithBlock(count, new byte[Math.Max(size, 0)], sizeOverride: size);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(ex.Message).Contains(message);
    }

    [Test]
    public async Task ABlockLargerThanTheLimit_IsRejectedBeforeItIsRead()
    {
        var bytes = WithBlock(1, [], sizeOverride: 1L << 40);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(ex.Message).Contains("larger than the limit");
    }

    [Test]
    public async Task ADeflateBlockThatExpandsPastTheLimit_IsRejected()
    {
        // 4 MB of zeros deflate to a few kilobytes.
        var zeros = new byte[4 * 1024 * 1024];
        using var compressed = new PooledBufferWriter();
        AvroCodec.Deflate.Compress(zeros, compressed);
        var bytes = WithBlock(1, compressed.WrittenSpan.ToArray(), codec: "deflate", schema: "\"bytes\"");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes), options: new AvroFileReaderOptions { MaxBlockLength = 1024 * 1024 });
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(compressed.WrittenCount).IsLessThan(64 * 1024);
        await Assert.That(ex.Message).Contains("decompressed block is larger than the limit");
    }

    [Test]
    public async Task ADeflateBlockJustBelowTheLimit_IsRead()
    {
        // A bytes value of 1 MiB minus its 3-byte length prefix: exactly the limit.
        var value = new byte[(1024 * 1024) - 3];
        using var datum = new PooledBufferWriter();
        var w = new AvroWriter(datum);
        w.WriteBytes(value);
        w.Flush();
        using var compressed = new PooledBufferWriter();
        AvroCodec.Deflate.Compress(datum.WrittenMemory, compressed);
        var bytes = WithBlock(1, compressed.WrittenSpan.ToArray(), codec: "deflate", schema: "\"bytes\"");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes), options: new AvroFileReaderOptions { MaxBlockLength = 1024 * 1024 });

        await Assert.That(datum.WrittenCount).IsEqualTo(1024 * 1024);
        await Assert.That(reader.ReadAll().Single().AsBytes().Length).IsEqualTo(value.Length);
    }

    [Test]
    public async Task CorruptDeflateData_IsRejected()
    {
        var bytes = WithBlock(1, [0xFF, 0xFF, 0xFF, 0xFF], codec: "deflate");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(ex.Message).Contains("cannot be decompressed");
    }

    [Test]
    public async Task BytesLeftAfterABlocksLastObject_AreRejected()
    {
        // Two longs, but the block says it holds one.
        var bytes = WithBlock(1, [0x02, 0x04]);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(ex.Message).Contains("1 bytes left after its last object");
    }

    [Test]
    public async Task ZeroSizeObjects_AreLimitedPerBlock()
    {
        var withinLimit = WithBlock(65_536, [], schema: "\"null\"");
        var overLimit = WithBlock(65_537, [], schema: "\"null\"");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(withinLimit));
        using var hostile = AvroFileReader.OpenGeneric(new MemoryStream(overLimit));
        var ex = Assert.Throws<AvroDataException>(() => hostile.ReadAll().ToList());

        await Assert.That(reader.ReadAll().Count()).IsEqualTo(65_536);
        await Assert.That(ex.Message).Contains("declares 65537 objects in 0 bytes");
    }

    /// <summary>A header for the schema <c>long</c> (or <paramref name="schema"/>) whose sync marker is all zeros.</summary>
    private static byte[] Header(string schema = "\"long\"", string codec = "null") => Build((ref w) =>
    {
        w.WriteRaw("Obj\u0001"u8);
        w.WriteBlockCount(2);
        w.WriteString("avro.schema");
        w.WriteString(schema);
        w.WriteString("avro.codec");
        w.WriteString(codec);
        w.WriteBlockEnd();
        w.WriteRaw(new byte[SyncSize]);
    });

    private static byte[] WithBlock(long count, byte[] data, long? sizeOverride = null, string codec = "null", string schema = "\"long\"")
    {
        var header = Header(schema, codec);
        return Build((ref w) =>
        {
            w.WriteRaw(header);
            w.WriteLong(count);
            w.WriteLong(sizeOverride ?? data.Length);
            w.WriteRaw(data);
            w.WriteRaw(new byte[SyncSize]);
        });
    }

    private delegate void WriteAction(ref AvroWriter writer);

    private static byte[] Build(WriteAction write)
    {
        using var output = new PooledBufferWriter();
        var writer = new AvroWriter(output);
        write(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }
}
