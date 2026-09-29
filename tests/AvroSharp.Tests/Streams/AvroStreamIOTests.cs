using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Streams;

namespace AvroSharp.Tests.Streams;

/// <summary>
/// The stream reader's and writer's I/O (#133): flushing, closing the stream, cancellation, and the promise that the
/// asynchronous members do no synchronous I/O.
/// </summary>
public class AvroStreamIOTests
{
    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"Item","namespace":"io","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"}]}""");

    [Test]
    public async Task Flush_WritesTheBufferedObjects_WithoutClosingTheWriter()
    {
        using var stream = new MemoryStream();
        using var writer = AvroStreamWriter.CreateGeneric(stream, s_schema, new AvroStreamOptions { LeaveOpen = true });
        foreach (var item in Items(3))
        {
            writer.Write(item);
        }

        await Assert.That(stream.Length).IsEqualTo(0);
        writer.Flush();
        await Assert.That(ReadAll(stream.ToArray()).Count).IsEqualTo(3);

        // The writer stays usable after a flush.
        writer.Write(Items(1)[0]);
        writer.Flush();
        await Assert.That(ReadAll(stream.ToArray()).Count).IsEqualTo(4);
    }

    [Test]
    public async Task TheAsynchronousWriter_WritesFlushesAndDisposes_WithNoSynchronousIO()
    {
        var stream = new AsyncOnlyStream();
        var writer = AvroStreamWriter.CreateGeneric(stream, s_schema, new AvroStreamOptions { BufferSize = 64, LeaveOpen = true });
        foreach (var item in Items(50))
        {
            await writer.WriteAsync(item);
        }

        await writer.FlushAsync();
        var flushed = stream.Length;
        await writer.DisposeAsync();

        await Assert.That(flushed).IsGreaterThan(0);
        await Assert.That(ReadAll(stream.ToArray()).Count).IsEqualTo(50);
    }

    [Test]
    public async Task TheAsynchronousReader_ReadsInSmallChunks_WithNoSynchronousIO()
    {
        var stream = new AsyncOnlyStream(Write(Items(40))) { MaxChunk = 7 };
        await using var reader = AvroStreamReader.OpenGeneric(stream, s_schema, options: new AvroStreamOptions { BufferSize = 32 });

        var read = new List<AvroValue>();
        await foreach (var value in reader.ReadAllAsync())
        {
            read.Add(value);
        }

        await Assert.That(read.Count).IsEqualTo(40);
        await Assert.That(stream.AsyncReads).IsGreaterThan(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposingTheWriter_ClosesTheStream_UnlessLeaveOpen(bool leaveOpen, bool asynchronously)
    {
        var stream = new AsyncOnlyStream { AllowSync = !asynchronously };
        var writer = AvroStreamWriter.CreateGeneric(stream, s_schema, new AvroStreamOptions { LeaveOpen = leaveOpen });
        writer.Write(Items(1)[0]);

        if (asynchronously)
        {
            await writer.DisposeAsync();
        }
        else
        {
            writer.Dispose();
        }

        await Assert.That(stream.Disposed).IsEqualTo(!leaveOpen);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposingTheReader_ClosesTheStream_UnlessLeaveOpen(bool leaveOpen, bool asynchronously)
    {
        var stream = new AsyncOnlyStream(Write(Items(2))) { AllowSync = true };
        var reader = AvroStreamReader.OpenGeneric(stream, s_schema, options: new AvroStreamOptions { LeaveOpen = leaveOpen });

        if (asynchronously)
        {
            await reader.DisposeAsync();
        }
        else
        {
            reader.Dispose();
        }

        await Assert.That(stream.Disposed).IsEqualTo(!leaveOpen);
    }

    [Test]
    public async Task ACancelledRead_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var reader = AvroStreamReader.OpenGeneric(new AsyncOnlyStream(Write(Items(3))), s_schema);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync(cancelled.Token))
            {
            }
        });
    }

    [Test]
    public async Task ACancelledWriteOrFlush_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The smallest buffer (16 bytes) fills within a few objects, and is then written with the cancelled token.
        await using var writer = AvroStreamWriter.CreateGeneric(new AsyncOnlyStream(), s_schema, new AvroStreamOptions { BufferSize = 16 });
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            foreach (var item in Items(10))
            {
                await writer.WriteAsync(item, cancelled.Token);
            }
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.FlushAsync(cancelled.Token));
    }

    private static List<GenericRecord> Items(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new GenericRecord(s_schema) { ["id"] = (long)i, ["name"] = $"item {i}" })];

    private static byte[] Write(IEnumerable<GenericRecord> items)
    {
        using var stream = new MemoryStream();
        using (var writer = AvroStreamWriter.CreateGeneric(stream, s_schema, new AvroStreamOptions { LeaveOpen = true }))
        {
            foreach (var item in items)
            {
                writer.Write(item);
            }
        }

        return stream.ToArray();
    }

    private static List<AvroValue> ReadAll(byte[] data)
    {
        using var reader = AvroStreamReader.OpenGeneric(new MemoryStream(data), s_schema);
        return [.. reader.ReadAll()];
    }
}
