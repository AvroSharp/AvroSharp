using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AvroSharp.Containers;

/// <summary>
/// The <c>deflate</c> codec: raw DEFLATE (RFC 1951) without zlib framing, as the specification requires, using the
/// BCL's <see cref="DeflateStream"/>.
/// </summary>
/// <remarks>Instances are immutable and thread-safe.</remarks>
public sealed class DeflateCodec : AvroCodec
{
    /// <summary>Creates a deflate codec.</summary>
    /// <param name="level">The compression level; it affects writing only.</param>
    public DeflateCodec(CompressionLevel level = CompressionLevel.Optimal) => Level = level;

    /// <summary>Gets the codec with <see cref="CompressionLevel.Optimal"/> compression, also <see cref="AvroCodec.Deflate"/>.</summary>
    public static DeflateCodec Default { get; } = new();

    /// <summary>Gets the compression level.</summary>
    public CompressionLevel Level { get; }

    /// <inheritdoc/>
    public override string Name => AvroCodecNames.Deflate;

    /// <inheritdoc/>
    public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var deflate = new DeflateStream(new BufferWriterStream(destination), Level, leaveOpen: false);
        var segment = AsSegment(source);
        deflate.Write(segment.Array!, segment.Offset, segment.Count);
    }

    /// <inheritdoc/>
    public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var segment = AsSegment(source);
        using var deflate = new DeflateStream(new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false), CompressionMode.Decompress);
#if NETSTANDARD2_0
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
            {
                destination.Write(chunk.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
#else
        while (true)
        {
            // No size hint: a block may end just below the reader's limit, which a large hint would exceed.
            var span = destination.GetSpan();
            var read = deflate.Read(span);
            if (read == 0)
            {
                return;
            }

            destination.Advance(read);
        }
#endif
    }

    private static ArraySegment<byte> AsSegment(ReadOnlyMemory<byte> memory) =>
        MemoryMarshal.TryGetArray(memory, out var segment) ? segment : new ArraySegment<byte>(memory.ToArray());
}
