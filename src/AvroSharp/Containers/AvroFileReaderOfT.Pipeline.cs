using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.IO;

namespace AvroSharp.Containers;

/// <summary>
/// Pipelined reading: a background task reads and decompresses blocks ahead of the caller, who decodes them, so
/// I/O and decompression overlap with decoding. Blocks pass between them through a bounded channel.
/// </summary>
public sealed partial class AvroFileReader<T>
{
    /// <summary>
    /// Reads the remaining objects with I/O and decompression running ahead of decoding: a background task reads and
    /// decompresses up to <paramref name="blocksAhead"/> blocks while the caller decodes the current one. This helps
    /// most with the slower codecs (xz, bzip2) and slow streams; with the null codec on a fast stream it adds only
    /// overhead.
    /// </summary>
    /// <remarks>
    /// While the enumeration runs, the reader belongs to it: do not call other members. Memory is bounded by about
    /// <paramref name="blocksAhead"/> + 2 blocks. Errors from reading or decompressing a block are reported when the
    /// caller reaches that block. Stopping the enumeration early stops the background task.
    /// </remarks>
    /// <param name="blocksAhead">How many decompressed blocks may wait for the caller, from 1.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <exception cref="AvroDataException">A block is malformed or truncated.</exception>
    public async IAsyncEnumerable<T> ReadAllPipelinedAsync(int blocksAhead = 2, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(blocksAhead, 1);

        // Objects of a block already being read are returned first.
        while (_objectsLeft > 0)
        {
            yield return DecodeNext();
        }

        var channel = Channel.CreateBounded<Block>(new BoundedChannelOptions(blocksAhead)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() => ProduceBlocksAsync(channel.Writer, stop.Token), CancellationToken.None);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var block))
                {
                    try
                    {
                        var position = 0;
                        for (var i = 0L; i < block.Count; i++)
                        {
                            yield return Decode(block.Data, ref position, i == block.Count - 1);
                        }
                    }
                    finally
                    {
                        block.Release();
                    }
                }
            }
        }
        finally
        {
            // Stops the producer if the caller stopped early, then returns the buffers of blocks nobody decoded.
            await CancelAsync(stop).ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: CancelAsync above stopped the producer. Its other failures were already handed to the reader.
            }

            ReleaseUnread(channel.Reader);
        }
    }

    private static async ValueTask CancelAsync(CancellationTokenSource source)
    {
#if NET8_0_OR_GREATER
        await source.CancelAsync().ConfigureAwait(false);
#else
#pragma warning disable CA1849, VSTHRD103 // CancelAsync does not exist here; the registered callbacks are brief.
        source.Cancel();
#pragma warning restore CA1849, VSTHRD103
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }

    private static void ReleaseUnread(ChannelReader<Block> reader)
    {
        while (reader.TryRead(out var unread))
        {
            unread.Release();
        }
    }

    private async Task ProduceBlocksAsync(ChannelWriter<Block> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (read, count, size) = await ReadRawBlockAsync(cancellationToken).ConfigureAwait(false);
                if (!read)
                {
                    break;
                }

                ConsumeSyncMarker();
                var block = TakeBlock(count, size);
                try
                {
                    await writer.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    block.Release();
                    throw;
                }
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
            throw;
        }
#pragma warning disable CA1031 // Every failure is handed to the caller, who rethrows it when it reaches this block.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            writer.TryComplete(ex);
        }
    }

    // Takes ownership of the raw block (the next block rents a new buffer) and decompresses it into a buffer of its own.
    private Block TakeBlock(long count, int size)
    {
        var raw = _raw;
        _raw = [];
        if (ReferenceEquals(_codec, AvroCodec.Null))
        {
            CheckObjectCount(count, size);
            return new Block(count, new ArraySegment<byte>(raw, 0, size), raw, null);
        }

        // A guess at the decompressed size, within the limit the decompressed block must meet anyway (#129).
        var decompressed = new PooledBufferWriter((int)Math.Max(Math.Min(size * 4L, _maxBlockLength), 256));
        try
        {
            DecompressInto(new ArraySegment<byte>(raw, 0, size), new LimitedBufferWriter(decompressed, _maxBlockLength));
            CheckObjectCount(count, decompressed.WrittenCount);
            var block = new Block(count, decompressed.WrittenSegment, null, decompressed);
            decompressed = null;
            return block;
        }
        finally
        {
            decompressed?.Dispose();
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private T Decode(ArraySegment<byte> data, ref int position, bool last)
    {
        var reader = new AvroReader(data.AsSpan(position));
        var value = _read(ref reader);
        position += (int)reader.BytesConsumed;
        if (last && position != data.Count)
        {
            throw new AvroDataException($"A block has {data.Count - position} bytes left after its last object.");
        }

        return value;
    }

    /// <summary>A block's objects and the buffer that holds them, pooled either as an array or as a writer.</summary>
    private sealed class Block(long count, ArraySegment<byte> data, byte[]? rented, PooledBufferWriter? writer)
    {
        public long Count { get; } = count;

        public ArraySegment<byte> Data { get; } = data;

        public void Release()
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            writer?.Dispose();
        }
    }
}
