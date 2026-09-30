using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;

namespace AvroSharp.Tests.Containers;

/// <summary>Pipelined container reading (#32): ReadAllPipelinedAsync, with reading and decompression ahead of decoding.</summary>
public class ContainerPipelineTests
{
    public static IEnumerable<(bool Deflate, int BlocksAhead)> Cases() => [(false, 1), (false, 4), (true, 1), (true, 4)];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PipelinedReading_ReturnsTheObjectsInOrder(bool deflate, int blocksAhead)
    {
        var rows = ContainerFileTests.Rows(400);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 256, Codec = deflate ? AvroCodec.Deflate : AvroCodec.Null });

        await using var reader = await AvroFileReader.OpenGenericAsync(new NoSyncReadStream(file));
        var read = new List<AvroValue>();
        await foreach (var value in reader.ReadAllPipelinedAsync(blocksAhead))
        {
            read.Add(value);
        }

        await Assert.That(read.SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task ObjectsOfABlockAlreadyStarted_ComeFirst()
    {
        var rows = ContainerFileTests.Rows(50);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 200 });
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        reader.TryRead(out var first);
        reader.TryRead(out var second);

        var rest = new List<AvroValue>();
        await foreach (var value in reader.ReadAllPipelinedAsync())
        {
            rest.Add(value);
        }

        await Assert.That(new[] { first, second }.Concat(rest).SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task StoppingEarly_StopsTheProducer()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(2000), new AvroFileWriterOptions { SyncInterval = 128, Codec = AvroCodec.Deflate });
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));

        var taken = 0;
        await foreach (var _ in reader.ReadAllPipelinedAsync(blocksAhead: 1))
        {
            if (++taken == 3)
            {
                break;
            }
        }

        await Assert.That(taken).IsEqualTo(3);
    }

    [Test]
    public async Task ACorruptBlock_IsReportedAfterTheBlocksBeforeIt()
    {
        var rows = ContainerFileTests.Rows(300);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 256 });
        using (var probe = AvroFileReader.OpenGeneric(new MemoryStream(file)))
        {
            probe.ReadAll().ToList();
        }

        // Damage the last sync marker: every block before the last one reads.
        file[^1] ^= 0xFF;
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        var read = 0;
        var ex = await Assert.ThrowsAsync<AvroDataException>(async () =>
        {
            await foreach (var _ in reader.ReadAllPipelinedAsync())
            {
                read++;
            }
        });

        await Assert.That(ex!.Message).Contains("sync marker");
        await Assert.That(read).IsGreaterThan(0);
        await Assert.That(read).IsLessThan(rows.Count);
    }

    [Test]
    public async Task Cancellation_StopsReading()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(500), new AvroFileWriterOptions { SyncInterval = 128 });
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        using var cancel = new CancellationTokenSource();

        var read = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadAllPipelinedAsync(cancellationToken: cancel.Token))
            {
                if (++read == 10)
                {
                    await cancel.CancelAsync();
                }
            }
        });

        await Assert.That(read).IsGreaterThanOrEqualTo(10);
        await Assert.That(read).IsLessThan(500);
    }

    [Test]
    public async Task BlocksAhead_MustBePositive()
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(ContainerFileTests.WriteFile(ContainerFileTests.Rows(1), AvroFileWriterOptions.Default)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in reader.ReadAllPipelinedAsync(blocksAhead: 0))
            {
            }
        });
    }

    /// <summary>A read-only memory stream whose synchronous reads throw, returning asynchronous reads in small chunks.</summary>
    private sealed class NoSyncReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read.");

#if NET
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous read.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Through the array overload of the base class: its span overload calls back into ours in a subclass.
            var chunk = new byte[Math.Min(buffer.Length, 1000)];
            var read = base.Read(chunk, 0, chunk.Length);
            chunk.AsSpan(0, read).CopyTo(buffer.Span);
            return new(read);
        }
#endif

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(base.Read(buffer, offset, Math.Min(count, 1000)));
    }
}
