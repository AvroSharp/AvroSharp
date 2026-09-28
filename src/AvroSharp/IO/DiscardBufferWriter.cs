using System;
using System.Buffers;

namespace AvroSharp.IO;

/// <summary>
/// An output that forgets what is written: where a span <see cref="AvroWriter"/> made for a Try* write continues after
/// its destination is full, so the value's remaining writes need no checks of their own.
/// </summary>
internal sealed class DiscardBufferWriter : IBufferWriter<byte>
{
    private const int MinimumSize = 256;

    // Per thread, so concurrent writers never share it; its contents are never read.
    [ThreadStatic]
    private static byte[]? s_scratch;

    private DiscardBufferWriter()
    {
    }

    public static DiscardBufferWriter Instance { get; } = new();

    public void Advance(int count)
    {
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Scratch(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Scratch(sizeHint);

    private static byte[] Scratch(int sizeHint)
    {
        var size = Math.Max(sizeHint, MinimumSize);
        var scratch = s_scratch;
        if (scratch is null || scratch.Length < size)
        {
            s_scratch = scratch = new byte[size];
        }

        return scratch;
    }
}
