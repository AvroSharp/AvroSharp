using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Containers;

/// <summary>Blocks as stored, concatenation, Sync, and appending to existing files (#173).</summary>
public class ContainerBlockTests
{
    private static readonly AvroFileWriterOptions NullCodec = new() { SyncInterval = 200 };
    private static readonly AvroFileWriterOptions DeflateCodec = new() { Codec = AvroCodec.Deflate, SyncInterval = 200 };

    [Test]
    public async Task Concat_OfOneCodec_CopiesEveryBlockByteForByte()
    {
        var rows = ContainerFileTests.Rows(120);
        var first = ContainerFileTests.WriteFile(rows.Take(70), DeflateCodec);
        var second = ContainerFileTests.WriteFile(rows.Skip(70), DeflateCodec);

        var output = new MemoryStream();
        AvroFile.Concat(output, [new MemoryStream(first), new MemoryStream(second)]);

        var expected = Blocks(first).Concat(Blocks(second)).ToList();
        var actual = Blocks(output.ToArray());
        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            await Assert.That(actual[i].Count).IsEqualTo(expected[i].Count);
            await Assert.That(actual[i].Data.ToArray()).IsEquivalentTo(expected[i].Data.ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }

        await Assert.That(ReadAll(output.ToArray()).SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task Concat_OfMixedCodecs_RecompressesToTheFirstFilesCodec()
    {
        var rows = ContainerFileTests.Rows(90);
        var deflated = ContainerFileTests.WriteFile(rows.Take(40), DeflateCodec);
        var plain = ContainerFileTests.WriteFile(rows.Skip(40), NullCodec);

        var output = new MemoryStream();
        AvroFile.Concat(output, [new MemoryStream(deflated), new MemoryStream(plain)]);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(output.ToArray()));
        await Assert.That(reader.Codec.Name).IsEqualTo("deflate");
        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task Concat_KeepsTheFirstFilesMetadata()
    {
        var options = new AvroFileWriterOptions { Metadata = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["app"] = "v1"u8.ToArray() } };
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), options);

        var output = new MemoryStream();
        AvroFile.Concat(output, [new MemoryStream(file), new MemoryStream(file)]);

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(output.ToArray()));
        await Assert.That(reader.TryGetMetadataString("app", out var app)).IsTrue();
        await Assert.That(app).IsEqualTo("v1");
        await Assert.That(reader.ReadAll().Count()).IsEqualTo(6);
    }

    [Test]
    public async Task Concat_RejectsAnotherSchema()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), NullCodec);
        var other = WriteInts([1, 2, 3], NullCodec);

        var ex = Assert.Throws<AvroException>(() => AvroFile.Concat(new MemoryStream(), [new MemoryStream(file), new MemoryStream(other)]));
        Assert.Throws<ArgumentException>(() => AvroFile.Concat(new MemoryStream(), []));

        await Assert.That(ex.Message).Contains("Input 1");
    }

    [Test]
    public async Task WriteBlock_RejectsABlockOfAnotherSchema()
    {
        var block = Blocks(WriteInts([1], NullCodec)).Single();
        using var writer = AvroFileWriter.CreateGeneric(new MemoryStream(), AvroSchema.Parse("\"long\""));

        var ex = Assert.Throws<ArgumentException>(() => writer.WriteBlock(block));

        await Assert.That(ex.ParamName).IsEqualTo("block");
    }

    [Test]
    public async Task WriteBlock_AfterObjects_KeepsTheirOrder_AndRecompressesWhenAsked()
    {
        var rows = ContainerFileTests.Rows(30);
        var blocks = Blocks(ContainerFileTests.WriteFile(rows.Skip(10), DeflateCodec));
        var output = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(output, rows[0].AsRecord().Schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate, LeaveOpen = true }))
        {
            foreach (var row in rows.Take(10))
            {
                writer.Write(row);
            }

            foreach (var block in blocks)
            {
                await writer.WriteBlockAsync(block, recompress: true);
            }
        }

        await Assert.That(ReadAll(output.ToArray()).SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task TryReadBlock_MixesWithObjects_AWholeBlockAtATime()
    {
        var rows = ContainerFileTests.Rows(60);
        var file = ContainerFileTests.WriteFile(rows, NullCodec);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));

        await Assert.That(reader.TryReadBlock(out var firstBlock)).IsTrue();
        await Assert.That(reader.TryRead(out var next)).IsTrue();
        var error = Assert.Throws<InvalidOperationException>(() => reader.TryReadBlock(out _));

        await Assert.That(next).IsEqualTo(rows[(int)firstBlock!.Count]);
        await Assert.That(error.Message).Contains("still unread");
        await Assert.That(firstBlock.Schema).IsSameReferenceAs(reader.WriterSchema);
        await Assert.That(firstBlock.Codec).IsSameReferenceAs(reader.Codec);
    }

    [Test]
    public async Task Sync_ReturnsAPositionTheReaderCanSeekTo()
    {
        var rows = ContainerFileTests.Rows(20);
        var output = new MemoryStream();
        long position;
        using (var writer = AvroFileWriter.CreateGeneric(output, rows[0].AsRecord().Schema, new AvroFileWriterOptions { LeaveOpen = true }))
        {
            foreach (var row in rows.Take(5))
            {
                writer.Write(row);
            }

            position = writer.Sync();
            foreach (var row in rows.Skip(5))
            {
                writer.Write(row);
            }

            await Assert.That(await writer.SyncAsync()).IsEqualTo(output.Length);
        }

        await Assert.That(position).IsLessThan(output.Length);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(output.ToArray()));
        reader.Seek(position);
        await Assert.That(reader.ReadAll().SequenceEqual(rows.Skip(5))).IsTrue();
    }

    [Test]
    [Arguments("null")]
    [Arguments("deflate")]
    public async Task Append_InSeveralSessions_ReadsAsOneFile(string codec)
    {
        var rows = ContainerFileTests.Rows(90);
        var options = string.Equals(codec, "null", StringComparison.Ordinal) ? NullCodec : DeflateCodec;
        var file = new MemoryStream();
        Fill(file, ContainerFileTests.WriteFile(rows.Take(30), options));

        // The default options stand for the file's codec when it is built in.
        using (var writer = AvroFileWriter.OpenAppendGeneric(file, options: new AvroFileWriterOptions { LeaveOpen = true }))
        {
            await Assert.That(writer.Codec.Name).IsEqualTo(codec);
            foreach (var row in rows.Skip(30).Take(30))
            {
                writer.Write(row);
            }
        }

        await using (var writer = await AvroFileWriter.OpenAppendGenericAsync(file, rows[0].AsRecord().Schema, new AvroFileWriterOptions { Codec = options.Codec, LeaveOpen = true }))
        {
            foreach (var row in rows.Skip(60))
            {
                await writer.WriteAsync(row);
            }
        }

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file.ToArray()));
        await Assert.That(reader.Codec.Name).IsEqualTo(codec);
        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task Append_ToAHeaderOnlyFile_AndToAnEmptyStream()
    {
        var rows = ContainerFileTests.Rows(4);
        var schema = rows[0].AsRecord().Schema;
        var headerOnly = new MemoryStream();
        Fill(headerOnly, ContainerFileTests.WriteFile([], NullCodec));
        var empty = new MemoryStream();

        foreach (var file in new[] { headerOnly, empty })
        {
            using (var writer = AvroFileWriter.OpenAppendGeneric(file, schema, new AvroFileWriterOptions { LeaveOpen = true }))
            {
                foreach (var row in rows)
                {
                    writer.Write(row);
                }
            }

            await Assert.That(ReadAll(file.ToArray()).SequenceEqual(rows)).IsTrue();
        }

        Assert.Throws<ArgumentException>(() => AvroFileWriter.OpenAppendGeneric(new MemoryStream()));
    }

    [Test]
    public async Task Append_KeepsTheFilesSyncMarker()
    {
        var file = new MemoryStream();
        Fill(file, ContainerFileTests.WriteFile(ContainerFileTests.Rows(2), NullCodec));
        using var original = AvroFileReader.OpenGeneric(new MemoryStream(file.ToArray()));

        using var writer = AvroFileWriter.OpenAppendGeneric(file, options: new AvroFileWriterOptions { LeaveOpen = true });

        await Assert.That(writer.SyncMarker.ToArray()).IsEquivalentTo(original.SyncMarker.ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Append_RejectsAnotherSchemaOrCodec()
    {
        var file = new MemoryStream();
        Fill(file, ContainerFileTests.WriteFile(ContainerFileTests.Rows(2), DeflateCodec));

        var schema = Assert.Throws<AvroException>(() => AvroFileWriter.OpenAppendGeneric(file, AvroSchema.Parse("\"int\"")));
        var codec = Assert.Throws<AvroException>(() => AvroFileWriter.OpenAppendGeneric(file, options: new AvroFileWriterOptions { Codec = new RenamedCodec("snappy") }));

        await Assert.That(schema.Message).Contains("same schema");
        await Assert.That(codec.Message).Contains("'deflate'");
    }

    [Test]
    public async Task Append_RejectsMetadataAndStreamsItCannotSeek()
    {
        var file = new MemoryStream();
        Fill(file, ContainerFileTests.WriteFile(ContainerFileTests.Rows(2), NullCodec));
        var withMetadata = new AvroFileWriterOptions { Metadata = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["a"] = new byte[1] } };

        var metadata = Assert.Throws<ArgumentException>(() => AvroFileWriter.OpenAppendGeneric(file, options: withMetadata));
        var stream = Assert.Throws<ArgumentException>(() => AvroFileWriter.OpenAppendGeneric(new UnseekableStream()));

        await Assert.That(metadata.ParamName).IsEqualTo("options");
        await Assert.That(stream.ParamName).IsEqualTo("stream");
    }

    [Test]
    public async Task Append_UsesTheOptionsCodec_WhenItIsTheFiles()
    {
        // A codec the reader doesn't know, given to the writer: appending needs only its name to match.
        var codec = new RenamedCodec("custom");
        var schema = ContainerFileTests.Rows(1)[0].AsRecord().Schema;
        var file = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions { Codec = codec, LeaveOpen = true }))
        {
            writer.Write(ContainerFileTests.Rows(1)[0]);
        }

        using (var writer = AvroFileWriter.OpenAppendGeneric(file, options: new AvroFileWriterOptions { Codec = codec, LeaveOpen = true }))
        {
            writer.Write(ContainerFileTests.Rows(2)[1]);
        }

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file.ToArray()), options: new AvroFileReaderOptions { Codecs = [codec] });
        await Assert.That(reader.ReadAll().SequenceEqual(ContainerFileTests.Rows(2))).IsTrue();
    }

    private static void Fill(MemoryStream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    private static List<AvroFileBlock> Blocks(byte[] file)
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        var blocks = new List<AvroFileBlock>();
        while (reader.TryReadBlock(out var block))
        {
            blocks.Add(block);
        }

        return blocks;
    }

    private static List<AvroValue> ReadAll(byte[] file)
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        return reader.ReadAll().ToList();
    }

    private static byte[] WriteInts(int[] values, AvroFileWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(stream, AvroSchema.Parse("\"int\""), options))
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }

        return stream.ToArray();
    }

    private sealed class UnseekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    // The null codec under another name.
    private sealed class RenamedCodec(string name) : AvroCodec
    {
        public override string Name => name;

        public override void Compress(ReadOnlyMemory<byte> source, System.Buffers.IBufferWriter<byte> destination) => AvroCodec.Null.Compress(source, destination);

        public override void Decompress(ReadOnlyMemory<byte> source, System.Buffers.IBufferWriter<byte> destination) => AvroCodec.Null.Decompress(source, destination);
    }
}
