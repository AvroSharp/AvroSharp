using System;
using System.Buffers;
using System.IO;
using AvroSharp.Containers;
using LzmaNet;

namespace AvroSharp.Codecs;

/// <summary>
/// The <c>xz</c> codec: each block is one XZ stream (LZMA2), with a CRC-64 check as Java's xz codec writes it.
/// Reading accepts every XZ check type.
/// </summary>
/// <remarks>Uses the fully managed Lzma.Net library. Instances are thread-safe.</remarks>
public sealed class XzCodec : AvroCodec
{
    /// <summary>The default compression preset, 6, the same as Java's <c>CodecFactory.xzCodec</c> and the <c>xz</c> tool.</summary>
    public const int DefaultLevel = 6;

    private readonly XzCompressOptions _options;

    /// <summary>Creates an xz codec.</summary>
    /// <param name="level">The compression preset, from 0 (fastest) to 9 (smallest); it affects writing only.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 0 to 9.</exception>
    public XzCodec(int level = DefaultLevel)
    {
        if (level is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The level must be from 0 to 9.");
        }

        Level = level;
        _options = new XzCompressOptions { Preset = level };
    }

    /// <summary>Gets the codec with the <see cref="DefaultLevel"/>.</summary>
    public static XzCodec Default { get; } = new();

    /// <summary>Gets the compression preset.</summary>
    public int Level { get; }

    /// <inheritdoc/>
    public override string Name => AvroCodecNames.Xz;

    /// <inheritdoc/>
    public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var xz = new XzCompressStream(new BufferWriterStream(destination), _options, leaveOpen: false);
        CodecStreams.Write(xz, source);
    }

    /// <inheritdoc/>
    public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            using var xz = new XzDecompressStream(CodecStreams.OpenRead(source), leaveOpen: false);
            CodecStreams.CopyTo(xz, destination);
        }
        catch (LzmaException ex)
        {
            throw new InvalidDataException($"The block is not valid XZ data: {ex.Message}", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The block ends inside an XZ stream.", ex);
        }
        catch (Exception ex) when (CodecStreams.IsCorruptData(ex))
        {
            throw new InvalidDataException($"The block is not valid XZ data: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (level {Level})";
}
