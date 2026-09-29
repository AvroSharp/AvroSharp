using System;
using System.Buffers;
using System.IO;
using AvroSharp.Containers;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.BZip2;

namespace AvroSharp.Codecs;

/// <summary>The <c>bzip2</c> codec: each block is one bzip2 stream.</summary>
/// <remarks>Uses the fully managed SharpZipLib library. Instances are thread-safe.</remarks>
public sealed class Bzip2Codec : AvroCodec
{
    /// <summary>The default block size, 9 (900 KB), the same as Java's bzip2 codec.</summary>
    public const int DefaultBlockSize = 9;

    /// <summary>Creates a bzip2 codec.</summary>
    /// <param name="blockSize">
    /// The bzip2 block size in units of 100 KB, from 1 to 9. Larger blocks compress better and use more memory; it
    /// affects writing only.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is outside 1 to 9.</exception>
    public Bzip2Codec(int blockSize = DefaultBlockSize)
    {
        if (blockSize is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "The block size must be from 1 to 9.");
        }

        BlockSize = blockSize;
    }

    /// <summary>Gets the codec with the <see cref="DefaultBlockSize"/>.</summary>
    public static Bzip2Codec Default { get; } = new();

    /// <summary>Gets the bzip2 block size in units of 100 KB.</summary>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public override string Name => AvroCodecNames.Bzip2;

    /// <inheritdoc/>
    public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var bzip2 = new BZip2OutputStream(new BufferWriterStream(destination), BlockSize);
        CodecStreams.Write(bzip2, source);
    }

    /// <inheritdoc/>
    public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            using var bzip2 = new BZip2InputStream(CodecStreams.OpenRead(source));
            CodecStreams.CopyTo(bzip2, destination);
        }
        catch (SharpZipBaseException ex)
        {
            throw new InvalidDataException($"The block is not valid bzip2 data: {ex.Message}", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The block ends inside a bzip2 stream.", ex);
        }
        catch (Exception ex) when (CodecStreams.IsCorruptData(ex))
        {
            throw new InvalidDataException($"The block is not valid bzip2 data: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (block size {BlockSize})";
}
