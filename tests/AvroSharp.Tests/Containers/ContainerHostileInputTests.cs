using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.Containers;
using AvroSharp.IO;
using TUnit.Assertions.Enums;

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
    public async Task BytesLeftAfterABlocksLastObject_AreRejected_WhenPipelined()
    {
        var bytes = WithBlock(1, [0x02, 0x04]);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = await Assert.ThrowsAsync<AvroDataException>(async () =>
        {
            await foreach (var _ in reader.ReadAllPipelinedAsync())
            {
            }
        });

        await Assert.That(ex!.Message).IsEqualTo("A block has 1 bytes left after its last object.");
    }

    /// <summary>A varint of more than 10 bytes cannot be a long: it is rejected, in the header and before a block.</summary>
    [Test]
    public async Task OverlongVarints_AreRejected()
    {
        var overlong = Enumerable.Repeat((byte)0x80, 11).ToArray();
        var inHeader = Build((ref w) =>
        {
            w.WriteRaw("Obj\u0001"u8);
            w.WriteRaw(overlong);
        });
        var beforeABlock = Header().Concat(overlong).ToArray();

        var header = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(inHeader)));
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(beforeABlock));
        var block = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());

        await Assert.That(header.Message).IsEqualTo("The metadata block count is a varint longer than 10 bytes.");
        await Assert.That(block.Message).IsEqualTo("The block count is a varint longer than 10 bytes.");
    }

    /// <summary>
    /// Zero-size objects are bounded by an option, counting each object's values (#129, #130): blocks of more than
    /// 65,536 nulls, which Java and earlier AvroSharp versions write, are read.
    /// </summary>
    [Test]
    public async Task ZeroSizeObjects_AreLimitedPerBlock_ByTheValuesTheyCreate()
    {
        const string ThreeNulls = """{"type":"record","name":"R","fields":[{"name":"a","type":"null"},{"name":"b","type":"null"},{"name":"c","type":"null"}]}""";
        var nulls = WithBlock(1_000_000, [], schema: "\"null\"");
        var records = WithBlock(4_194_305, [], schema: ThreeNulls);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(nulls));
        await Assert.That(reader.ReadAll().Count()).IsEqualTo(1_000_000);

        // Each record creates four values, so 4,194,305 of them exceed 16,777,216.
        using var hostile = AvroFileReader.OpenGeneric(new MemoryStream(records));
        var ex = Assert.Throws<AvroDataException>(() => hostile.ReadAll().ToList());
        await Assert.That(ex.Message).Contains("declares 4194305 objects in 0 bytes (AvroFileReaderOptions.MaxZeroSizeValuesPerBlock)");

        using var strict = AvroFileReader.OpenGeneric(new MemoryStream(nulls), options: new AvroFileReaderOptions { MaxZeroSizeValuesPerBlock = 999_999 });
        Assert.Throws<AvroDataException>(() => strict.ReadAll().ToList());
    }

    [Test]
    public async Task ObjectsThatTakeABytePerObject_CannotOutnumberTheBlocksBytes()
    {
        // A union's branch index takes a byte, so a union of null cannot be zero-size.
        var bytes = WithBlock(3, [0x00, 0x00], schema: """["null","long"]""");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().ToList());
        await Assert.That(ex.Message).IsEqualTo("A block declares 3 objects in 2 bytes.");
    }

    [Test]
    public async Task TheWriter_StartsANewBlockEvery65536Objects()
    {
        var output = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(output, AvroSharp.Schemas.AvroSchema.Parse("\"null\"")))
        {
            for (var i = 0; i < 65_537; i++)
            {
                writer.Write(AvroSharp.Generic.AvroValue.Null);
            }
        }

        // The strictest reader of zero-size items still reads each block.
        var options = new AvroFileReaderOptions { MaxZeroSizeValuesPerBlock = 65_536 };
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(output.ToArray()), options: options);
        await Assert.That(reader.ReadAll().Count()).IsEqualTo(65_537);
    }

    [Test]
    public async Task AHeaderOfManyMetadataEntries_IsRejected()
    {
        // 2,000 empty keys and values, two bytes each (#129).
        var bytes = Build((ref w) =>
        {
            w.WriteRaw("Obj\u0001"u8);
            w.WriteBlockCount(2);
            w.WriteString("avro.schema");
            w.WriteString("\"long\"");
            w.WriteString("avro.codec");
            w.WriteString("null");
            w.WriteBlockCount(2_000);
            for (var i = 0; i < 2_000; i++)
            {
                w.WriteString(string.Empty);
                w.WriteString(string.Empty);
            }

            w.WriteBlockEnd();
            w.WriteRaw(new byte[SyncSize]);
        });

        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));

        await Assert.That(ex.Message).Contains("more than 1024 metadata entries");
    }

    [Test]
    public async Task ASchemaLongerThanTheLimit_IsRejected()
    {
        var schema = """{"type":"record","name":"R","fields":[{"name":"a","type":"long"}]}""";
        var bytes = WithBlock(1, [0x02], schema: schema);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes), options: new AvroFileReaderOptions { MaxSchemaLength = schema.Length });
        var ex = Assert.Throws<AvroDataException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes), options: new AvroFileReaderOptions { MaxSchemaLength = schema.Length - 1 }));

        await Assert.That(reader.ReadAll().Count()).IsEqualTo(1);
        await Assert.That(ex.Message).Contains("AvroFileReaderOptions.MaxSchemaLength");
    }

    /// <summary>
    /// An empty block (0 objects in 0 bytes) between two blocks is skipped and the records after it are read. This
    /// pins a difference from Apache.Avro C# 1.12.2, which stops at the empty block and reads only the first record:
    /// that is an Apache bug, since the specification allows any number of objects in a block.
    /// </summary>
    [Test]
    public async Task AnEmptyBlockBetweenTwoBlocks_IsSkipped()
    {
        var header = Header();
        var bytes = Build((ref w) =>
        {
            w.WriteRaw(header);
            w.WriteLong(1);
            w.WriteLong(1);
            w.WriteLong(5);
            w.WriteRaw(new byte[SyncSize]);
            w.WriteLong(0);
            w.WriteLong(0);
            w.WriteRaw(new byte[SyncSize]);
            w.WriteLong(1);
            w.WriteLong(1);
            w.WriteLong(-6);
            w.WriteRaw(new byte[SyncSize]);
        });

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));

        await Assert.That(reader.ReadAll().Select(v => v.AsInt64()).ToArray()).IsEquivalentTo(new[] { 5L, -6L }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An empty avro.codec is not taken as "null", which only an absent key means: it is an unknown codec, rejected
    /// when the file is opened like any other codec the reader lacks.
    /// </summary>
    [Test]
    public async Task AnEmptyCodecName_IsRejected()
    {
        var bytes = WithBlock(1, [0x02], codec: string.Empty);

        var ex = Assert.Throws<AvroException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));

        await Assert.That(ex.Message).IsEqualTo("The file is compressed with the '' codec, which is not available; add it to AvroFileReaderOptions.Codecs.");
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
