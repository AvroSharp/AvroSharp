using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Codecs.Tests;

/// <summary>
/// The codecs of AvroSharp.Codecs (#32): Java's files for every codec, round trips, corrupt data and the block limit.
/// </summary>
public class CodecTests
{
    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"Row","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"data","type":"bytes"}]}""");

    public static IEnumerable<Func<AvroCodec>> Codecs() =>
    [
        () => SnappyCodec.Default,
        () => ZstandardCodec.Default,
        () => new ZstandardCodec(level: 19, checksum: true),
        () => new ZstandardCodec(level: -5),
        () => Bzip2Codec.Default,
        () => new Bzip2Codec(blockSize: 1),
        () => XzCodec.Default,
        () => new XzCodec(level: 0),
    ];

    public static IEnumerable<string> JavaCodecs() => ["deflate", "snappy", "bzip2", "xz", "zstandard"];

    [Test]
    [MethodDataSource(nameof(JavaCodecs))]
    public async Task JavasWeatherFile_InEveryCodec_HoldsTheRecordsOfApachesWeatherFile(string codec)
    {
        var expected = ReadAll("apache-avro/weather.avro");

        using var reader = Open("java-avro/weather-" + codec + ".avro");
        var records = reader.ReadAll().ToList();

        await Assert.That(reader.Codec).IsEqualTo(codec);
        await Assert.That(records).IsEquivalentTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(JavaCodecs))]
    public async Task JavasManyBlockFile_InEveryCodec_HoldsTheSameRecords(string codec)
    {
        // Deflate is built into the core, so it is the reference for the codecs of this package.
        var expected = ReadAll("java-avro/many-deflate.avro");

        using var reader = Open("java-avro/many-" + codec + ".avro");
        var records = reader.ReadAll().ToList();

        await Assert.That(reader.Codec).IsEqualTo(codec);
        await Assert.That(records.Count).IsEqualTo(8000);
        await Assert.That(records.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(JavaCodecs))]
    public async Task JavasManyBlockFile_ReadsTheSame_Pipelined(string codec)
    {
        var expected = ReadAll("java-avro/many-" + codec + ".avro");

        await using var reader = await AvroFileReader.OpenGenericAsync(File.OpenRead(TestData.PathOf("java-avro/many-" + codec + ".avro")), options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
        var read = new List<AvroValue>();
        await foreach (var value in reader.ReadAllPipelinedAsync())
        {
            read.Add(value);
        }

        await Assert.That(read.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task ApachesSnappyWeatherFile_IsRead()
    {
        var expected = ReadAll("apache-avro/weather.avro");

        using var reader = Open("apache-avro/weather-snappy.avro");

        await Assert.That(reader.Codec).IsEqualTo("snappy");
        await Assert.That(reader.ReadAll().ToList()).IsEquivalentTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task FilesOfSeveralBlocks_RoundTrip(AvroCodec codec)
    {
        var values = Rows(500);

        var file = Write(codec, values, syncInterval: 1000);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file), options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });

        await Assert.That(reader.Codec).IsEqualTo(codec.Name);
        await Assert.That(reader.ReadAll().Select(v => v.AsRecord()).SequenceEqual(values)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task EmptyAndIncompressibleBlocks_RoundTrip(AvroCodec codec)
    {
        // One row per block: a row with no data, and one of random bytes that does not compress.
        var random = new Random(7);
        var noise = new byte[70_000];
        random.NextBytes(noise);
        var values = new[] { Row(0, string.Empty, []), Row(1, "noise", noise) };

        var file = Write(codec, values, syncInterval: 1);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file), options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });

        await Assert.That(reader.ReadAll().Select(v => v.AsRecord()).SequenceEqual(values)).IsTrue();
    }

    /// <summary>
    /// The codecs whose blocks carry an integrity check. Zstandard frames have one only when written with a checksum;
    /// without it, damaged data may decode to other bytes, as in Java.
    /// </summary>
    public static IEnumerable<Func<AvroCodec>> CheckedCodecs() =>
    [
        () => SnappyCodec.Default,
        () => new ZstandardCodec(checksum: true),
        () => Bzip2Codec.Default,
        () => XzCodec.Default,
    ];

    [Test]
    [MethodDataSource(nameof(CheckedCodecs))]
    public async Task CorruptBlocks_AreAvroDataExceptions(AvroCodec codec)
    {
        var file = Write(codec, Rows(200), syncInterval: 1 << 20);
        var blockStart = HeaderLength(file);

        // Damage the compressed data: past the block's count and size varints, in the middle of the block.
        var damaged = (byte[])file.Clone();
        var middle = blockStart + ((file.Length - 16 - blockStart) / 2);
        for (var i = middle; i < middle + 8; i++)
        {
            damaged[i] ^= 0x5A;
        }

        var ex = await Assert.ThrowsAsync<AvroDataException>(() => Task.Run(() => ReadAllOf(damaged)));

        await Assert.That(ex!.Message).Contains(codec.Name);
    }

    [Test]
    public async Task ASnappyBlockWithAWrongChecksum_IsRejected()
    {
        var file = Write(SnappyCodec.Default, Rows(10), syncInterval: 1 << 20);

        // The last block ends with its 4-byte CRC-32 and then the 16-byte sync marker.
        file[file.Length - 17] ^= 0x01;
        var ex = await Assert.ThrowsAsync<AvroDataException>(() => Task.Run(() => ReadAllOf(file)));

        await Assert.That(ex!.Message).Contains("CRC-32");
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task BlocksThatDecompressPastTheLimit_AreRejected(AvroCodec codec)
    {
        // 200 KB of zeros compresses to little, so only the decompressed size can exceed the limit.
        var file = Write(codec, [Row(1, "zeros", new byte[200_000])], syncInterval: 1 << 20);

        var ex = await Assert.ThrowsAsync<AvroDataException>(() => Task.Run(() =>
        {
            using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file), options: new AvroFileReaderOptions
            {
                Codecs = AvroCodecs.All,
                MaxBlockLength = 100_000,
            });
            _ = reader.ReadAll().ToList();
        }));

        await Assert.That(ex!.Message).Contains("MaxBlockLength");
    }

    [Test]
    public async Task AFileOfAStandardCodecThatIsNotRegistered_NamesThisPackage()
    {
        var file = Write(ZstandardCodec.Default, Rows(3), syncInterval: 1 << 20);

        var ex = Assert.Throws<AvroException>(() => AvroFileReader.OpenGeneric(new MemoryStream(file)));

        await Assert.That(ex.Message).Contains("AvroSharp.Codecs");
        await Assert.That(ex.Message).Contains("AvroCodecs.All");
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task TruncatedBlocks_AreInvalidData(AvroCodec codec)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("truncated block ", 500)));
        var compressed = new System.Buffers.ArrayBufferWriter<byte>();
        codec.Compress(data, compressed);

        // Cut at several points, including inside a snappy block's 4-byte checksum and a block shorter than it.
        foreach (var length in new[] { 0, 3, compressed.WrittenCount / 2, compressed.WrittenCount - 2 })
        {
            var cut = compressed.WrittenMemory[..length];
            Assert.Throws<InvalidDataException>(() => codec.Decompress(cut, new System.Buffers.ArrayBufferWriter<byte>()));
        }

        await Assert.That(compressed.WrittenCount).IsGreaterThan(4);
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task BlocksNotBackedByAnArray_RoundTrip(AvroCodec codec)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("unmanaged memory ", 300)));
        using var source = new NotAnArray(data);
        var compressed = new System.Buffers.ArrayBufferWriter<byte>();
        codec.Compress(source.Memory, compressed);

        using var block = new NotAnArray(compressed.WrittenSpan.ToArray());
        var decompressed = new System.Buffers.ArrayBufferWriter<byte>();
        codec.Decompress(block.Memory, decompressed);

        await Assert.That(decompressed.WrittenSpan.SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task InvalidSettings_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstandardCodec(ZstandardCodec.MaxLevel + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstandardCodec(ZstandardCodec.MinLevel - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bzip2Codec(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bzip2Codec(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XzCodec(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XzCodec(10));

        await Assert.That(AvroCodecs.All.Select(c => c.Name)).IsEquivalentTo(new[] { "snappy", "zstandard", "bzip2", "xz" });
    }

    private static AvroFileReader<AvroValue> Open(string testData) =>
        AvroFileReader.OpenGeneric(File.OpenRead(TestData.PathOf(testData)), options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });

    private static List<AvroValue> ReadAll(string testData)
    {
        using var reader = Open(testData);
        return reader.ReadAll().ToList();
    }

    private static void ReadAllOf(byte[] file)
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file), options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
        _ = reader.ReadAll().ToList();
    }

    private static byte[] Write(AvroCodec codec, IEnumerable<GenericRecord> values, int syncInterval)
    {
        using var file = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(file, s_schema, new AvroFileWriterOptions { Codec = codec, SyncInterval = syncInterval, LeaveOpen = true }))
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }

        return file.ToArray();
    }

    /// <summary>The length of the header, which ends with the 16-byte sync marker that also ends every block.</summary>
    private static int HeaderLength(byte[] file)
    {
        var marker = file.AsSpan(file.Length - 16);
        for (var i = 4; i + 16 <= file.Length; i++)
        {
            if (file.AsSpan(i, 16).SequenceEqual(marker))
            {
                return i + 16;
            }
        }

        throw new InvalidOperationException("No sync marker.");
    }

    private static List<GenericRecord> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => Row(i, $"row {i % 17}", BitConverter.GetBytes(i * 7919L))).ToList();

    private static GenericRecord Row(long id, string name, byte[] data) => new(s_schema) { ["id"] = id, ["name"] = name, ["data"] = data };

    /// <summary>Memory that does not expose an array (as native memory would not), for the codecs' copying paths.</summary>
    private sealed class NotAnArray(byte[] data) : System.Buffers.MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => data;

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override bool TryGetArray(out ArraySegment<byte> segment)
        {
            segment = default;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
