using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AvroSharp.Tests;

/// <summary>
/// A memory stream that throws on synchronous I/O (unless allowed) and returns reads in small chunks. Its async
/// members call the base class's synchronous members directly, which the checks do not see.
/// </summary>
internal sealed class AsyncOnlyStream : MemoryStream
{
    public AsyncOnlyStream()
    {
    }

    public AsyncOnlyStream(byte[] data)
    {
        base.Write(data, 0, data.Length);
        Position = 0;
    }

    public bool AllowSync { get; set; }

    public int MaxChunk { get; set; } = int.MaxValue;

    public int AsyncReads { get; private set; }

    public bool Disposed { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckSync();
        return base.Read(buffer, offset, Math.Min(count, MaxChunk));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckSync();
        base.Write(buffer, offset, count);
    }

    public override void Flush() => CheckSync();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AsyncReads++;
        return Task.FromResult(base.Read(buffer, offset, Math.Min(count, MaxChunk)));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        base.Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

#if NET
    public override int Read(Span<byte> buffer)
    {
        CheckSync();
        var chunk = new byte[Math.Min(buffer.Length, MaxChunk)];
        var read = base.Read(chunk, 0, chunk.Length);
        chunk.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckSync();
        var array = buffer.ToArray();
        base.Write(array, 0, array.Length);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AsyncReads++;
        // MemoryStream's span overloads call the virtual array overloads in a subclass; go through an array.
        var chunk = new byte[Math.Min(buffer.Length, MaxChunk)];
        var read = base.Read(chunk, 0, chunk.Length);
        chunk.AsSpan(0, read).CopyTo(buffer.Span);
        return new(read);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var array = buffer.ToArray();
        base.Write(array, 0, array.Length);
        return default;
    }
#endif

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    private void CheckSync()
    {
        if (!AllowSync)
        {
            throw new InvalidOperationException("Synchronous I/O on an async-only stream.");
        }
    }
}
