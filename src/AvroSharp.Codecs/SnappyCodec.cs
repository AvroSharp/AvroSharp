using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using AvroSharp.Containers;
using Snappier;

namespace AvroSharp.Codecs;

/// <summary>
/// The <c>snappy</c> codec: each block is Snappy-compressed and followed by the 4-byte big-endian CRC-32 of the
/// uncompressed data, as the specification requires. The checksum is verified on read.
/// </summary>
/// <remarks>Uses the fully managed Snappier library. Instances are thread-safe.</remarks>
public sealed class SnappyCodec : AvroCodec
{
    private const int ChecksumLength = 4;

    // Above snappy's largest expansion: a 3-byte copy of 64 bytes.
    private const int MaxExpansion = 32;

    private SnappyCodec()
    {
    }

    /// <summary>Gets the snappy codec. Snappy has no compression levels.</summary>
    public static SnappyCodec Default { get; } = new();

    /// <inheritdoc/>
    public override string Name => AvroCodecNames.Snappy;

    /// <inheritdoc/>
    public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var data = source.Span;
        var span = destination.GetSpan(Snappy.GetMaxCompressedLength(data.Length) + ChecksumLength);
        var length = Snappy.Compress(data, span);
        BinaryPrimitives.WriteUInt32BigEndian(span[length..], Crc32.HashToUInt32(data));
        destination.Advance(length + ChecksumLength);
    }

    /// <inheritdoc/>
    public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var block = source.Span;
        if (block.Length < ChecksumLength)
        {
            throw new InvalidDataException($"The block has {block.Length} bytes, too few for its 4-byte CRC-32.");
        }

        var compressed = block[..^ChecksumLength];
        // The preamble is a varint that the library returns as an int, so 2^31 or more arrives negative (#129).
        // Snappy expands at most about 21 times, so a larger length is corrupt, however it would be read.
        int length, written;
        Span<byte> span;
        try
        {
            length = Snappy.GetUncompressedLength(compressed);
            if (length < 0 || length > (long)compressed.Length * MaxExpansion)
            {
                throw new InvalidDataException($"The block declares {length} uncompressed bytes in {compressed.Length} compressed ones.");
            }

            span = destination.GetSpan(length)[..length];
            written = Snappy.Decompress(compressed, span);
        }
        catch (Exception ex) when (CodecStreams.IsCorruptData(ex))
        {
            throw new InvalidDataException($"The block is not valid Snappy data: {ex.Message}", ex);
        }

        var data = span[..written];

        var expected = BinaryPrimitives.ReadUInt32BigEndian(block[^ChecksumLength..]);
        var actual = Crc32.HashToUInt32(data);
        if (actual != expected)
        {
            throw new InvalidDataException($"The block's CRC-32 is 0x{expected:X8}, but its data has 0x{actual:X8}.");
        }

        destination.Advance(written);
    }
}
