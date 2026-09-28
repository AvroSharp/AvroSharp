using System;
using System.Buffers;
using System.IO;
using AvroSharp.Containers;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AvroSharp.Codecs;

/// <summary>
/// The <c>zstandard</c> codec: each block is one or more Zstandard frames. Reading accepts frames with or without
/// a content checksum and with or without the decompressed size, as Java's streaming writer produces them.
/// </summary>
/// <remarks>
/// Uses the fully managed ZstdSharp library. Instances are thread-safe: compression and decompression contexts are
/// kept per thread and reused.
/// </remarks>
public sealed class ZstandardCodec : AvroCodec
{
    /// <summary>The default compression level, 3, the same as Java's <c>CodecFactory.zstandardCodec</c>.</summary>
    public const int DefaultLevel = 3;

    [ThreadStatic]
    private static Compressor? s_compressor;

    [ThreadStatic]
    private static Decompressor? s_decompressor;

    /// <summary>Creates a zstandard codec.</summary>
    /// <param name="level">
    /// The compression level, from <see cref="MinLevel"/> (fastest) to <see cref="MaxLevel"/> (smallest); it affects
    /// writing only.
    /// </param>
    /// <param name="checksum">
    /// Whether each frame carries a checksum of its content, verified when it is read. Without one (the default, as in
    /// Java), a damaged block may decompress to other bytes instead of failing.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside the supported range.</exception>
    public ZstandardCodec(int level = DefaultLevel, bool checksum = false)
    {
        if (level < MinLevel || level > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"The level must be from {MinLevel} to {MaxLevel}.");
        }

        Level = level;
        Checksum = checksum;
    }

    /// <summary>Gets the codec with the <see cref="DefaultLevel"/> and no checksum.</summary>
    public static ZstandardCodec Default { get; } = new();

    /// <summary>Gets the lowest (fastest) compression level.</summary>
    public static int MinLevel => Compressor.MinCompressionLevel;

    /// <summary>Gets the highest (smallest output) compression level.</summary>
    public static int MaxLevel => Compressor.MaxCompressionLevel;

    /// <summary>Gets the compression level.</summary>
    public int Level { get; }

    /// <summary>Gets whether written frames carry a checksum of their content.</summary>
    public bool Checksum { get; }

    /// <inheritdoc/>
    public override string Name => AvroCodecNames.Zstandard;

    /// <inheritdoc/>
    public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var compressor = s_compressor ??= new Compressor(Level);
        compressor.Level = Level;
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, Checksum ? 1 : 0);

        var data = source.Span;
        var span = destination.GetSpan(Compressor.GetCompressBound(data.Length));
        destination.Advance(compressor.Wrap(data, span));
    }

    /// <inheritdoc/>
    public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var decompressor = s_decompressor ??= new Decompressor();

        // Streaming, because a frame need not record its decompressed size. A previous block that failed may have
        // left the context mid-frame.
        decompressor.ResetStream();
        var input = source.Span;
        while (true)
        {
            // No size hint: a block may end just below the reader's limit, which a large hint would exceed.
            var status = decompressor.UnwrapStream(input, destination.GetSpan(), out var consumed, out var written);
            destination.Advance(written);
            input = input[consumed..];
            switch (status)
            {
                case OperationStatus.Done:
                    return;
                case OperationStatus.DestinationTooSmall:
                    continue;
                case OperationStatus.NeedMoreData:
                    throw new InvalidDataException("The block ends inside a Zstandard frame.");
                default:
                    throw new InvalidDataException("The block is not valid Zstandard data.");
            }
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (level {Level}{(Checksum ? ", checksum" : string.Empty)})";
}
