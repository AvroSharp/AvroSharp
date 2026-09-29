using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.IO;

namespace AvroSharp.Tests.Containers;

/// <summary>
/// Asynchronous container reading and writing (#32): the same files as the synchronous path, no synchronous I/O
/// (checked with a stream that throws on it), data arriving in small uneven chunks, and cancellation.
/// </summary>
public class ContainerAsyncTests
{
    [Test]
    [Arguments("null", 1)]
    [Arguments("null", 65536)]
    [Arguments("deflate", 700)]
    public async Task Records_RoundTripAsynchronously_WithoutSynchronousIO(string codec, int syncInterval)
    {
        var rows = ContainerFileTests.Rows(400);
        var file = new AsyncOnlyStream();
        var options = new AvroFileWriterOptions { Codec = string.Equals(codec, "null", StringComparison.Ordinal) ? AvroCodec.Null : AvroCodec.Deflate, SyncInterval = syncInterval, LeaveOpen = true };
        await using (var writer = AvroFileWriter.CreateGeneric(file, ContainerFileTests.Rows(1)[0].AsRecord().Schema, options))
        {
            foreach (var row in rows)
            {
                await writer.WriteAsync(row);
            }
        }

        file.Position = 0;
        file.MaxChunk = 7;
        await using var reader = await AvroFileReader.OpenGenericAsync(file);
        var read = await ToListAsync(reader.ReadAllAsync());

        await Assert.That(reader.Codec).IsEqualTo(codec);
        await Assert.That(read.SequenceEqual(rows)).IsTrue();
        await Assert.That(file.AsyncReads).IsGreaterThan(0);
    }

    [Test]
    public async Task FilesWrittenSynchronously_AreReadAsynchronously_AndTheOtherWayRound()
    {
        var rows = ContainerFileTests.Rows(200);
        var syncFile = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 300 });

        await using var reader = await AvroFileReader.OpenGenericAsync(new MemoryStream(syncFile));
        var asyncRead = await ToListAsync(reader.ReadAllAsync());

        var asyncFile = new MemoryStream();
        await using (var writer = AvroFileWriter.CreateGeneric(asyncFile, reader.WriterSchema, new AvroFileWriterOptions { SyncInterval = 300, LeaveOpen = true }))
        {
            foreach (var row in rows)
            {
                await writer.WriteAsync(row);
            }

            await writer.FlushAsync();
        }

        asyncFile.Position = 0;
        using var syncReader = AvroFileReader.OpenGeneric(asyncFile);

        await Assert.That(asyncRead.SequenceEqual(rows)).IsTrue();
        await Assert.That(syncReader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task AnEmptyWriter_DisposedAsynchronously_WritesTheHeader()
    {
        var file = new AsyncOnlyStream();
        await using (AvroFileWriter.CreateGeneric(file, ContainerFileTests.Rows(1)[0].AsRecord().Schema, new AvroFileWriterOptions { LeaveOpen = true }))
        {
        }

        file.Position = 0;
        await using var reader = await AvroFileReader.OpenGenericAsync(file);

        await Assert.That((await ToListAsync(reader.ReadAllAsync())).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AHeaderLargerThanTheInputBuffer_IsRead(bool async)
    {
        // 100 KB of metadata, delivered a few bytes at a time: the header is parsed again after each geometric fill.
        var big = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), new AvroFileWriterOptions { Metadata = new Dictionary<string, byte[]> { ["big"] = big } });
        var stream = new AsyncOnlyStream(bytes) { MaxChunk = 5, AllowSync = !async };

        int count;
        byte[] metadata;
        if (async)
        {
            await using var reader = await AvroFileReader.OpenGenericAsync(stream);
            count = (await ToListAsync(reader.ReadAllAsync())).Count;
            metadata = reader.Metadata["big"].ToArray();
        }
        else
        {
            using var reader = AvroFileReader.OpenGeneric(stream);
            count = reader.ReadAll().Count();
            metadata = reader.Metadata["big"].ToArray();
        }

        await Assert.That(metadata.SequenceEqual(big)).IsTrue();
        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task ReadingIsCancelled()
    {
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(100), new AvroFileWriterOptions { SyncInterval = 100 });
        using var cancellation = new CancellationTokenSource();
        await using var reader = await AvroFileReader.OpenGenericAsync(new AsyncOnlyStream(bytes));

        var read = 0;
        var cancelled = false;
        try
        {
            await foreach (var _ in reader.ReadAllAsync(cancellation.Token))
            {
                if (++read == 10)
                {
                    cancellation.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
        await Assert.That(read).IsLessThan(100);
    }

    [Test]
    public async Task OpeningIsCancelled()
    {
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(1), AvroFileWriterOptions.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new AsyncOnlyStream(bytes);

        var cancelled = false;
        try
        {
            await AvroFileReader.OpenGenericAsync(stream, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
        await Assert.That(stream.Disposed).IsTrue();
    }

    [Test]
    public async Task MalformedFiles_FailTheSameWayAsynchronously()
    {
        var bytes = ContainerFileTests.WriteFile(ContainerFileTests.Rows(3), AvroFileWriterOptions.Default);
        var badSync = bytes.ToArray();
        badSync[^1] ^= 0xFF;
        var truncated = bytes.AsSpan(0, bytes.Length - 3).ToArray();

        await using var syncMismatch = await AvroFileReader.OpenGenericAsync(new AsyncOnlyStream(badSync));
        await using var cutShort = await AvroFileReader.OpenGenericAsync(new AsyncOnlyStream(truncated));
        var notAFile = await ThrowsAsync(async () => await AvroFileReader.OpenGenericAsync(new AsyncOnlyStream([1, 2, 3])));

        await Assert.That((await ThrowsAsync(() => ToListAsync(syncMismatch.ReadAllAsync()))).Message).Contains("sync marker");
        await Assert.That((await ThrowsAsync(() => ToListAsync(cutShort.ReadAllAsync()))).Message).Contains("ends inside");
        await Assert.That(notAFile.Message).Contains("not an Avro object container file");
    }

    private static async Task<AvroDataException> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AvroDataException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected an AvroDataException.");
    }

    private static async Task<List<AvroValue>> ToListAsync(IAsyncEnumerable<AvroValue> values)
    {
        var list = new List<AvroValue>();
        await foreach (var value in values)
        {
            list.Add(value);
        }

        return list;
    }
}
