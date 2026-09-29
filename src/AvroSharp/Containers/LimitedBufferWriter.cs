using System;
using System.Buffers;
using AvroSharp.Buffers;

namespace AvroSharp.Containers;

/// <summary>
/// Receives a decompressed block and refuses to hold more than a limit, so a small compressed block cannot expand
/// into unbounded memory whatever the codec.
/// </summary>
internal sealed class LimitedBufferWriter(PooledBufferWriter inner, int limit) : IBufferWriter<byte>
{
    public void Advance(int count)
    {
        if (count > limit - inner.WrittenCount)
        {
            throw TooLarge();
        }

        inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(Check(sizeHint));

    public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(Check(sizeHint));

    private int Check(int sizeHint)
    {
        var room = limit - inner.WrittenCount;
        if (sizeHint > room)
        {
            throw TooLarge();
        }

        return sizeHint;
    }

    private AvroDataException TooLarge() =>
        new($"A decompressed block is larger than the limit of {limit} bytes (AvroFileReaderOptions.MaxBlockLength).");
}
