using System;
using System.Buffers;

namespace AvroSharp.Buffers;

/// <summary>A growable <see cref="IBufferWriter{T}"/> of bytes backed by <see cref="ArrayPool{T}.Shared"/>.</summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 16));
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>Discards everything written; the buffer is kept for reuse.</summary>
    public void Clear() => _written = 0;

    /// <summary>Discards what was written after the first <paramref name="length"/> bytes.</summary>
    public void Truncate(int length)
    {
        if ((uint)length > (uint)_written)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _written = length;
    }

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_written));
        _written += value.Length;
    }

    public void Write(byte value)
    {
        Ensure(1);
        _buffer[_written++] = value;
    }

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (sizeHint <= _buffer.Length - _written)
        {
            return;
        }

        var newSize = Math.Max(checked(_written + sizeHint), _buffer.Length * 2);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
