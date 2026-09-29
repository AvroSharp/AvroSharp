using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Containers;
using TUnit.Assertions.Enums;

namespace AvroSharp.Tests.Containers;

/// <summary>How container files use their streams (#62, #70): writes per block, retries, length checks and header fills.</summary>
public class ContainerIOTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EachBlock_IsOneStreamWrite(bool deflate)
    {
        var stream = new CountingStream();
        var options = new AvroFileWriterOptions { SyncInterval = 100, LeaveOpen = true, Codec = deflate ? AvroCodec.Deflate : AvroCodec.Null };
        using (var writer = AvroFileWriter.CreateGeneric(stream, ContainerFileTests.Rows(1).First().AsRecord().Schema, options))
        {
            foreach (var row in ContainerFileTests.Rows(60))
            {
                writer.Write(row);
            }
        }

        var blocks = CountBlocks(stream.ToArray());

        // The header, then one write per block.
        await Assert.That(blocks).IsGreaterThan(5);
        await Assert.That(stream.Writes).IsEqualTo(blocks + 1);
    }

    [Test]
    public async Task EachBlock_IsOneAsynchronousStreamWrite()
    {
        var stream = new CountingStream();
        await using (var writer = AvroFileWriter.CreateGeneric(stream, ContainerFileTests.Rows(1).First().AsRecord().Schema, new AvroFileWriterOptions { SyncInterval = 100, LeaveOpen = true }))
        {
            foreach (var row in ContainerFileTests.Rows(60))
            {
                await writer.WriteAsync(row);
            }
        }

        await Assert.That(stream.Writes).IsEqualTo(CountBlocks(stream.ToArray()) + 1);
    }

    [Test]
    public async Task ABlockWhoseWriteFailed_IsWrittenOnceWhenFlushedAgain()
    {
        var rows = ContainerFileTests.Rows(5);
        var stream = new CountingStream { FailWrite = 2 };
        var writer = AvroFileWriter.CreateGeneric(stream, rows[0].AsRecord().Schema, new AvroFileWriterOptions { LeaveOpen = true });
        foreach (var row in rows)
        {
            writer.Write(row);
        }

        // The header is written, then the block's write fails before any of it reaches the stream.
        Assert.Throws<IOException>(writer.Flush);
        writer.Dispose();

        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(stream.ToArray()));

        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task PastSync_ReadsTheStreamLengthOncePerBlock()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(60), new AvroFileWriterOptions { SyncInterval = 400 });
        var stream = new CountingStream(file);
        using var reader = AvroFileReader.OpenGeneric(stream);

        reader.Sync(0);
        var read = 0;
        while (reader.TryRead(out _) && !reader.PastSync(file.Length))
        {
            read++;
        }

        await Assert.That(read).IsEqualTo(60);
        await Assert.That(stream.LengthReads).IsLessThanOrEqualTo(CountBlocks(file) + 1);
    }

    [Test]
    public async Task AHeaderThatArrivesOneByteAtATime_KeepsLargeMetadata()
    {
        var big = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31)).ToArray();
        var rows = ContainerFileTests.Rows(3);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions
        {
            Metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["app.big"] = big, ["app.small"] = [1, 2, 3] },
        });

        using var reader = AvroFileReader.OpenGeneric(new CountingStream(file) { MaxRead = 1 });

        await Assert.That(reader.Metadata["app.big"].ToArray().SequenceEqual(big)).IsTrue();
        await Assert.That(reader.Metadata["app.small"].ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(reader.ReadAll().SequenceEqual(rows)).IsTrue();
    }

    // Counts the file's blocks by reading it.
    private static int CountBlocks(byte[] file)
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        var blocks = 0;
        var previous = -1L;
        while (reader.TryRead(out _))
        {
            if (reader.PreviousSync != previous)
            {
                previous = reader.PreviousSync;
                blocks++;
            }
        }

        return blocks;
    }

    /// <summary>A memory stream that counts writes and length reads, can fail one write, and can limit reads.</summary>
    private sealed class CountingStream : MemoryStream
    {
        public CountingStream()
        {
        }

        public CountingStream(byte[] data)
            : base(data, writable: false)
        {
        }

        public int Writes { get; private set; }

        public int LengthReads { get; private set; }

        /// <summary>Gets or sets the write (1-based) that throws, before writing anything; 0 for none.</summary>
        public int FailWrite { get; init; }

        public int MaxRead { get; init; } = int.MaxValue;

        public override long Length
        {
            get
            {
                LengthReads++;
                return base.Length;
            }
        }

        // Every write goes through here: the caller's one is counted, and MemoryStream's own calls back into the
        // array overload (from its span and asynchronous writes, in a subclass) are not.
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_depth == 0 && ++Writes == FailWrite)
            {
                throw new IOException("The write failed.");
            }

            _depth++;
            try
            {
                base.Write(buffer, offset, count);
            }
            finally
            {
                _depth--;
            }
        }

#if NET
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return default;
        }

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, MaxRead)]);
#endif

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, MaxRead));

        private int _depth;
    }
}
