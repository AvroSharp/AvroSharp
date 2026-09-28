using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace AvroSharp.Codecs;

/// <summary>A write-only <see cref="Stream"/> over an <see cref="IBufferWriter{T}"/>, for compression libraries that write to streams.</summary>
internal sealed class BufferWriterStream(IBufferWriter<byte> destination) : Stream
{
    // Stream plumbing that no codec calls: the codecs only write. Excluded from coverage rather than tested for its own sake.
    [ExcludeFromCodeCoverage]
    public override bool CanRead => false;

    [ExcludeFromCodeCoverage]
    public override bool CanSeek => false;

    [ExcludeFromCodeCoverage]
    public override bool CanWrite => true;

    [ExcludeFromCodeCoverage]
    public override long Length => throw new NotSupportedException();

    [ExcludeFromCodeCoverage]
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer.AsSpan(offset, count));

#if !NETSTANDARD2_0
    public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);
#endif

    public override void WriteByte(byte value) => destination.Write([value]);

    public override void Flush()
    {
    }

    [ExcludeFromCodeCoverage]
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    [ExcludeFromCodeCoverage]
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    [ExcludeFromCodeCoverage]
    public override void SetLength(long value) => throw new NotSupportedException();
}
