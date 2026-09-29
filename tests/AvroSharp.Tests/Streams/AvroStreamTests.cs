using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Streams;

namespace AvroSharp.Tests.Streams;

/// <summary>Streams of objects without a container (#32): AvroStreamWriter and AvroStreamReader.</summary>
public class AvroStreamTests
{
    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"Event","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"tags","type":{"type":"array","items":"string"}},{"name":"ok","type":"boolean"}]}""");

    [Test]
    [Arguments(16, int.MaxValue)]
    [Arguments(64, 1)]
    [Arguments(AvroStreamOptions.DefaultBufferSize, 7)]
    public async Task ObjectsRoundTrip_AcrossBufferBoundaries(int bufferSize, int maxRead)
    {
        var events = Events(500);
        var bytes = Write(events, new AvroStreamOptions { BufferSize = bufferSize });

        using var reader = AvroStreamReader.OpenGeneric(new LimitedStream(bytes, maxRead), s_schema, options: new AvroStreamOptions { BufferSize = bufferSize });
        var read = reader.ReadAll().ToList();

        await Assert.That(read.Count).IsEqualTo(events.Count);
        await Assert.That(read.Select(v => v.AsRecord()).SequenceEqual(events)).IsTrue();
        await Assert.That(reader.Position).IsEqualTo(bytes.Length);
    }

    [Test]
    public async Task ObjectsRoundTrip_Asynchronously()
    {
        var events = Events(300);
        using var file = new MemoryStream();
        await using (var writer = AvroStreamWriter.CreateGeneric(file, s_schema, new AvroStreamOptions { BufferSize = 100, LeaveOpen = true }))
        {
            foreach (var e in events)
            {
                await writer.WriteAsync(e);
            }
        }

        await using var reader = AvroStreamReader.OpenGeneric(new LimitedStream(file.ToArray(), 5), s_schema, options: new AvroStreamOptions { BufferSize = 32 });
        var read = new List<GenericRecord>();
        await foreach (var value in reader.ReadAllAsync())
        {
            read.Add(value.AsRecord());
        }

        await Assert.That(read.SequenceEqual(events)).IsTrue();
    }

    [Test]
    public async Task TheBytesAreTheObjectsEncodingsOneAfterAnother()
    {
        var events = Events(3);
        var bytes = Write(events, AvroStreamOptions.Default);

        var writer = GenericDatumWriter.Create(s_schema);
        var expected = events.SelectMany(e => writer.WriteToArray(e)).ToArray();

        await Assert.That(bytes.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task AnEmptyStream_HasNoObjects()
    {
        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream([]), s_schema);

        await Assert.That(reader.TryRead(out _)).IsFalse();
        await Assert.That(reader.Position).IsEqualTo(0);
    }

    [Test]
    public async Task AStreamThatEndsInsideAnObject_IsReportedWithTheObjectsOffset()
    {
        var bytes = Write(Events(10), AvroStreamOptions.Default);
        var firstNine = Write(Events(9), AvroStreamOptions.Default).Length;

        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(bytes.AsSpan(0, bytes.Length - 2).ToArray()), s_schema, options: new AvroStreamOptions { BufferSize = 16 });
        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().Count());

        await Assert.That(ex.Message).Contains($"stream offset {firstNine}");
    }

    [Test]
    public async Task CorruptData_IsReportedOnceTheStreamEnds()
    {
        var bytes = Write(Events(10), AvroStreamOptions.Default);

        // The first object's boolean is its last byte; 7 is not a boolean.
        var first = GenericDatumWriter.Create(s_schema).WriteToArray(Events(1)[0]).Length;
        bytes[first - 1] = 7;
        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(bytes), s_schema, options: new AvroStreamOptions { BufferSize = 16 });

        var ex = Assert.Throws<AvroDataException>(() => reader.ReadAll().Count());

        await Assert.That(ex.Message).Contains("stream offset 0");
        await Assert.That(ex.Message).Contains("boolean");
    }

    [Test]
    public async Task AnObjectLargerThanTheLimit_IsRejected_WithoutReadingTheRest()
    {
        var big = new GenericRecord(s_schema) { ["id"] = 1L, ["name"] = new string('x', 10_000), ["tags"] = AvroValue.FromArray([]), ["ok"] = true };
        var bytes = Write([big, .. Events(1000)], AvroStreamOptions.Default);
        var stream = new LimitedStream(bytes, int.MaxValue);

        using var reader = AvroStreamReader.OpenGeneric(stream, s_schema, options: new AvroStreamOptions { BufferSize = 256, MaxDatumLength = 4096 });
        var ex = Assert.Throws<AvroDataException>(() => reader.TryRead(out _));

        await Assert.That(ex.Message).Contains("MaxDatumLength");
        await Assert.That(stream.Position).IsLessThanOrEqualTo(4096);
    }

    [Test]
    public async Task ObjectsOfNoBytes_CannotBeRead()
    {
        var nullSchema = AvroSchema.Parse("\"null\"");
        using var file = new MemoryStream();
        using (var writer = AvroStreamWriter.CreateGeneric(file, AvroSchema.Parse("\"int\""), new AvroStreamOptions { LeaveOpen = true }))
        {
            writer.Write(1);
        }

        // Some data, but a schema whose objects take no bytes: there is no telling how many objects it holds.
        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(file.ToArray()), nullSchema);
        var ex = Assert.Throws<AvroDataException>(() => reader.TryRead(out _));

        await Assert.That(ex.Message).Contains("no bytes");
    }

    [Test]
    public async Task ObjectsAreResolvedToAReaderSchema()
    {
        var v2 = (RecordSchema)AvroSchema.Parse(
            """{"type":"record","name":"Event","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"level","type":"int","default":3}]}""");
        var bytes = Write(Events(20), AvroStreamOptions.Default);

        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(bytes), s_schema, v2, new AvroStreamOptions { BufferSize = 16 });
        var read = reader.ReadAll().Select(v => v.AsRecord()).ToList();

        await Assert.That(read.Count).IsEqualTo(20);
        await Assert.That(read.All(r => ReferenceEquals(r.Schema, v2) && r["level"].AsInt32() == 3)).IsTrue();
        await Assert.That(read[19]["name"].AsString()).IsEqualTo(Events(20)[19]["name"].AsString());
    }

    [Test]
    public async Task AnObjectThatFailsToEncode_LeavesTheWriterUsable()
    {
        var events = Events(2);
        using var file = new MemoryStream();
        using (var writer = AvroStreamWriter.CreateGeneric(file, s_schema, new AvroStreamOptions { LeaveOpen = true }))
        {
            writer.Write(events[0]);
            Assert.Throws<AvroException>(() => writer.Write(new GenericRecord(s_schema) { ["id"] = 1L }));
            writer.Write(events[1]);
        }

        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(file.ToArray()), s_schema);

        await Assert.That(reader.ReadAll().Select(v => v.AsRecord()).SequenceEqual(events)).IsTrue();
    }

    [Test]
    public async Task Options_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvroStreamOptions { BufferSize = 15 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvroStreamOptions { MaxDatumLength = 0 });
        Assert.Throws<ArgumentException>(() => AvroStreamReader.OpenGeneric(new WriteOnlyStream(), s_schema));
        Assert.Throws<ArgumentException>(() => AvroStreamWriter.CreateGeneric(new MemoryStream([], writable: false), s_schema));

        await Assert.That(AvroStreamOptions.Default.BufferSize).IsEqualTo(64 * 1024);
    }

    private static byte[] Write(IEnumerable<GenericRecord> events, AvroStreamOptions options)
    {
        using var file = new MemoryStream();
        using (var writer = AvroStreamWriter.CreateGeneric(file, s_schema, new AvroStreamOptions { BufferSize = options.BufferSize, LeaveOpen = true }))
        {
            foreach (var e in events)
            {
                writer.Write(e);
            }
        }

        return file.ToArray();
    }

    /// <summary>
    /// A stream that returns one byte per read made each read decode the object again from its start: O(n^2) for a
    /// long string (#129). A failed decode now says how many bytes the object needs, and the reader waits for them.
    /// </summary>
    [Test]
    public async Task AStreamThatReturnsOneBytePerRead_DecodesALongValueAFewTimes()
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        writer.WriteString(new string('x', 100_000));
        writer.WriteString("end");
        writer.Flush();

        var attempts = 0;
        using var reader = AvroStreamReader.Open(new LimitedStream(output.WrittenSpan.ToArray(), 1), (ref r) =>
        {
            attempts++;
            return (r.ReadString(), r.ReadString());
        });

        await Assert.That(reader.TryRead(out var value)).IsTrue();
        await Assert.That(value.Item1.Length).IsEqualTo(100_000);
        await Assert.That(value.Item2).IsEqualTo("end");
        await Assert.That(attempts).IsLessThan(20);
    }

    private static List<GenericRecord> Events(int count) =>
        Enumerable.Range(0, count).Select(i => new GenericRecord(s_schema)
        {
            ["id"] = (long)i * 1_000_003,
            ["name"] = new string((char)('a' + (i % 26)), i % 40),
            ["tags"] = AvroValue.FromArray(Enumerable.Range(0, i % 5).Select(t => (AvroValue)$"t{t}").ToArray()),
            ["ok"] = i % 3 == 0,
        }).ToList();

    /// <summary>A read-only stream that returns at most a given number of bytes per read.</summary>
    private sealed class LimitedStream(byte[] data, int maxRead) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maxRead));

#if NET
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
#endif
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
