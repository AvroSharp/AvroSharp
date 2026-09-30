using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Containers;

/// <summary>Object container files (M5, #32): Apache's test files, round trips, codecs and metadata.</summary>
public class ContainerFileTests
{
    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"Row","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"tags","type":{"type":"array","items":"string"}}]}""");

    [Test]
    [Arguments("weather.avro", "null")]
    [Arguments("weather-sorted.avro", "deflate")]
    public async Task ApacheWeatherFiles_HoldTheRecordsOfWeatherJson(string file, string codec)
    {
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(TestData.PathOf("apache-avro/" + file)));
        var records = reader.ReadAll().ToList();

        var json = GenericDatumJsonReader.Create(reader.WriterSchema);
        var expected = WeatherJsonLines().Select(json.Read).ToList();

        await Assert.That(reader.Codec.Name).IsEqualTo(codec);
        await Assert.That(((NamedSchema)reader.WriterSchema).FullName).IsEqualTo("test.Weather");
        await Assert.That(records.Count).IsEqualTo(expected.Count);
        // weather-sorted.avro holds the same records in another order, compressed with deflate by Java.
        foreach (var record in records)
        {
            await Assert.That(expected.Contains(record)).IsTrue();
        }
    }

    [Test]
    public async Task SyncInMeta_WhoseMetadataContainsTheSyncMarker_IsRead()
    {
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(TestData.PathOf("apache-avro/syncInMeta.avro")));

        var count = reader.ReadAll().Count();

        await Assert.That(count).IsGreaterThan(0);
    }

    [Test]
    public async Task ACodecThatIsNotAvailable_IsNamedInTheError()
    {
        var ex = Assert.Throws<AvroException>(() => AvroFileReader.OpenGeneric(File.OpenRead(TestData.PathOf("apache-avro/weather-snappy.avro"))));

        await Assert.That(ex.Message).Contains("'snappy' codec");
    }

    [Test]
    [Arguments("null", 1)]
    [Arguments("null", 65536)]
    [Arguments("deflate", 1)]
    [Arguments("deflate", 1000)]
    [Arguments("deflate", 65536)]
    public async Task Records_RoundTrip(string codec, int syncInterval)
    {
        var rows = Rows(500);
        var options = new AvroFileWriterOptions { Codec = string.Equals(codec, "null", StringComparison.Ordinal) ? AvroCodec.Null : AvroCodec.Deflate, SyncInterval = syncInterval };

        var bytes = WriteFile(rows, options);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));

        await Assert.That(reader.Codec.Name).IsEqualTo(codec);
        await Assert.That(reader.WriterSchema.CanonicalForm).IsEqualTo(s_schema.CanonicalForm);
        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task Deflate_CompressesRepetitiveData()
    {
        var rows = Rows(2000);

        var plain = WriteFile(rows, new AvroFileWriterOptions { Codec = AvroCodec.Null });
        var deflated = WriteFile(rows, new AvroFileWriterOptions { Codec = new DeflateCodec(CompressionLevel.Fastest) });

        await Assert.That(deflated.Length).IsLessThan(plain.Length / 2);

        // The built-in codec has the add-on codecs' shape: Default, and the level it was made with.
        await Assert.That(AvroCodec.Deflate).IsSameReferenceAs(DeflateCodec.Default);
        await Assert.That(DeflateCodec.Default.Level).IsEqualTo(CompressionLevel.Optimal);
        await Assert.That(new DeflateCodec(CompressionLevel.Fastest).Level).IsEqualTo(CompressionLevel.Fastest);
    }

    /// <summary>A level the runtime doesn't have fails when the codec is made, not when a writer's first block is.</summary>
    [Test]
    public async Task Deflate_RejectsALevelTheRuntimeDoesNotHave()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateCodec((CompressionLevel)42));

        await Assert.That(ex.ParamName).IsEqualTo("level");
        await Assert.That(new DeflateCodec(CompressionLevel.NoCompression).Level).IsEqualTo(CompressionLevel.NoCompression);
    }

    [Test]
    public async Task AFileWithoutObjects_HasOnlyAHeader()
    {
        var bytes = WriteFile([], AvroFileWriterOptions.Default);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes));

        await Assert.That(reader.TryRead(out _)).IsFalse();
        await Assert.That(reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Metadata_IsWrittenAndRead()
    {
        var options = new AvroFileWriterOptions
        {
            Metadata = new Dictionary<string, ReadOnlyMemory<byte>> { ["app.owner"] = Encoding.UTF8.GetBytes("team-a"), ["app.raw"] = new byte[] { 0, 255 } },
        };

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(WriteFile(Rows(1), options)));

        await Assert.That(reader.TryGetMetadataString("app.owner", out var owner)).IsTrue();
        await Assert.That(owner).IsEqualTo("team-a");
        await Assert.That(reader.Metadata["app.raw"].ToArray().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
        await Assert.That(reader.TryGetMetadataString("avro.codec", out var codec)).IsTrue();
        await Assert.That(codec).IsEqualTo("null");
        await Assert.That(reader.TryGetMetadataString("missing", out var missing)).IsFalse();
        await Assert.That(missing).IsNull();
    }

    [Test]
    public async Task ReservedMetadataKeys_AreRejected()
    {
        var options = new AvroFileWriterOptions { Metadata = new Dictionary<string, ReadOnlyMemory<byte>> { ["avro.custom"] = new byte[] { 1 } } };

        var ex = Assert.Throws<ArgumentException>(() => AvroFileWriter.CreateGeneric(new MemoryStream(), s_schema, options));

        await Assert.That(ex.Message).Contains("reserved");
    }

    [Test]
    public async Task AWriteThatThrows_LeavesNothingBehind()
    {
        using var stream = new MemoryStream();
        var datumWriter = GenericDatumWriter.Create(s_schema);
        using (var writer = AvroFileWriter.Create<AvroValue>(stream, s_schema, (ref w, value) =>
        {
            datumWriter.Write(ref w, value);
            // Commit the bytes before failing, as a large value would: the writer must still discard them.
            w.Flush();
            if (value.AsRecord()["id"].AsInt64() == 1)
            {
                throw new InvalidOperationException("fails after writing the record");
            }
        }, new AvroFileWriterOptions { LeaveOpen = true }))
        {
            var rows = Rows(3);
            writer.Write(rows[0]);
            Assert.Throws<InvalidOperationException>(() => writer.Write(rows[1]));
            writer.Write(rows[2]);
        }

        stream.Position = 0;
        using var reader = AvroFileReader.OpenGeneric(stream);
        var ids = reader.ReadAll().Select(r => r.AsRecord()["id"].AsInt64()).ToList();

        await Assert.That(ids.SequenceEqual(new long[] { 0, 2 })).IsTrue();
    }

    [Test]
    public async Task AReaderSchema_IsResolvedAgainstTheFileSchema()
    {
        var readerSchema = AvroSchema.Parse(
            """{"type":"record","name":"Row","namespace":"test","fields":[{"name":"name","type":"string"},{"name":"score","type":"double","default":0.5}]}""");

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(WriteFile(Rows(2), AvroFileWriterOptions.Default)), readerSchema);
        var records = reader.ReadAll().Select(r => r.AsRecord()).ToList();

        await Assert.That(records.Select(r => r["name"].AsString()).SequenceEqual(new[] { "row 0", "row 1" })).IsTrue();
        await Assert.That(records[1]["score"].AsDouble()).IsEqualTo(0.5);
    }

    [Test]
    public async Task ACustomCodec_IsUsedByName_AndCanReplaceABuiltInOne()
    {
        var rows = Rows(50);
        var xor = new XorCodec("xor");

        var bytes = WriteFile(rows, new AvroFileWriterOptions { Codec = xor });
        var ex = Assert.Throws<AvroException>(() => AvroFileReader.OpenGeneric(new MemoryStream(bytes)));
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(bytes), options: new AvroFileReaderOptions { Codecs = [xor] });

        await Assert.That(ex.Message).Contains("'xor' codec");
        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();

        // A codec named "deflate" in the options wins over the built-in one; this one cannot decode real deflate data.
        var deflated = WriteFile(rows, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
        using var replaced = AvroFileReader.OpenGeneric(new MemoryStream(deflated), options: new AvroFileReaderOptions { Codecs = [new XorCodec("deflate")] });
        Assert.Throws<AvroDataException>(() => replaced.ReadAll().ToList());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dispose_ClosesTheStream_UnlessLeaveOpen(bool leaveOpen)
    {
        var output = new MemoryStream();
        AvroFileWriter.CreateGeneric(output, s_schema, new AvroFileWriterOptions { LeaveOpen = leaveOpen }).Dispose();
        var input = new MemoryStream(WriteFile(Rows(1), AvroFileWriterOptions.Default));
        AvroFileReader.OpenGeneric(input, options: new AvroFileReaderOptions { LeaveOpen = leaveOpen }).Dispose();

        await Assert.That(output.CanWrite).IsEqualTo(leaveOpen);
        await Assert.That(input.CanRead).IsEqualTo(leaveOpen);
    }

    [Test]
    public async Task AStreamReturningOneByteAtATime_IsRead()
    {
        var rows = Rows(300);
        var bytes = WriteFile(rows, new AvroFileWriterOptions { Codec = AvroCodec.Deflate, SyncInterval = 500 });

        using var reader = AvroFileReader.OpenGeneric(new TrickleStream(bytes));

        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task Flush_WritesTheBufferedBlock()
    {
        using var stream = new MemoryStream();
        using var writer = AvroFileWriter.CreateGeneric(stream, s_schema);
        var headerLength = stream.Length;

        writer.Write(Rows(1)[0]);
        var beforeFlush = stream.Length;
        writer.Flush();

        await Assert.That(beforeFlush).IsEqualTo(headerLength);
        await Assert.That(stream.Length).IsGreaterThan(headerLength);
    }

    [Test]
    public async Task UsingADisposedWriterOrReader_Throws()
    {
        var writer = AvroFileWriter.CreateGeneric(new MemoryStream(), s_schema);
        writer.Dispose();
        var reader = AvroFileReader.OpenGeneric(new MemoryStream(WriteFile(Rows(1), AvroFileWriterOptions.Default)));
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.Write(Rows(1)[0]));
        Assert.Throws<ObjectDisposedException>(() => reader.TryRead(out _));
        writer.Dispose();
        reader.Dispose();
        await Task.CompletedTask;
    }

    private static string[] WeatherJsonLines() =>
        File.ReadAllLines(TestData.PathOf("apache-avro/weather.json")).Where(l => l.Length > 0).ToArray();

    internal static List<AvroValue> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => (AvroValue)new GenericRecord(s_schema)
        {
            ["id"] = (long)i,
            ["name"] = $"row {i}",
            ["tags"] = AvroValue.FromArray(Enumerable.Range(0, i % 5).Select(t => (AvroValue)$"tag {t}").ToList()),
        }).ToList();

    internal static byte[] WriteFile(IEnumerable<AvroValue> rows, AvroFileWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(stream, s_schema, options))
        {
            foreach (var row in rows)
            {
                writer.Write(row);
            }
        }

        return stream.ToArray();
    }

    private sealed class XorCodec(string name) : AvroCodec
    {
        public override string Name => name;

        public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination) => Xor(source, destination);

        public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination) => Xor(source, destination);

        private static void Xor(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
        {
            var span = destination.GetSpan(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = (byte)(source.Span[i] ^ 0x5A);
            }

            destination.Advance(source.Length);
        }
    }

    /// <summary>A stream that returns at most one byte per read.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

#if NET
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
#endif
    }
}
