using System;
using System.Buffers;

namespace AvroSharp.Tests.IO;

/// <summary>An IBufferWriter that hands out spans of exactly the requested size (at least <paramref name="chunkSize"/>), to force every buffer-boundary path.</summary>
internal sealed class TinyBufferWriter(int chunkSize) : IBufferWriter<byte>
{
    private byte[] _data = new byte[64];
    private int _written;

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _data.AsSpan(0, _written);

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint).Span;

    private Memory<byte> Reserve(int sizeHint)
    {
        var size = Math.Max(sizeHint, chunkSize);
        if (_data.Length - _written < size)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _written + size));
        }

        // Hand out exactly the requested size (or the chunk size), never more.
        return _data.AsMemory(_written, size);
    }
}
