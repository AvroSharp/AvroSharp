using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using AvroSharp.Buffers;
using AvroSharp.Containers;

namespace AvroSharp.Messages;

/// <summary>
/// The zlib format (RFC 1950) that AWS Glue's serializer uses (Java's <c>Deflater</c>): a two-byte header, raw
/// DEFLATE data and an Adler-32 checksum. Written by hand because <c>ZLibStream</c> is not in netstandard2.0.
/// </summary>
internal static class Zlib
{
    // CMF 0x78 (deflate, 32 KiB window) and FLG 0x9C (default compression level, no dictionary), as Java writes.
    private const byte Cmf = 0x78;
    private const byte Flg = 0x9C;

    public static void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination)
    {
        destination.Write([Cmf, Flg]);
        AvroCodec.Deflate.Compress(source, destination);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Adler32(source.Span));
        destination.Write(checksum);
    }

    /// <summary>Decompresses zlib data into <paramref name="destination"/>, at most <paramref name="maxLength"/> bytes.</summary>
    /// <exception cref="AvroDataException">The data is not zlib, is corrupt, or expands past the limit.</exception>
    public static void Decompress(ReadOnlyMemory<byte> source, PooledBufferWriter destination, int maxLength)
    {
        var span = source.Span;
        if (span.Length < 6 || (span[0] & 0x0F) != 8 || (span[0] >> 4) > 7 || ((span[0] << 8) | span[1]) % 31 != 0 || (span[1] & 0x20) != 0)
        {
            throw new AvroDataException("The compressed data is not in the zlib format (or uses a preset dictionary).");
        }

        try
        {
            AvroCodec.Deflate.Decompress(source[2..^4], new LimitedBufferWriter(destination, maxLength));
        }
        catch (InvalidDataException ex)
        {
            throw new AvroDataException($"The compressed data is corrupt: {ex.Message}", ex);
        }

        if (Adler32(destination.WrittenSpan) != BinaryPrimitives.ReadUInt32BigEndian(span[^4..]))
        {
            throw new AvroDataException("The compressed data does not match its Adler-32 checksum.");
        }
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint a = 1;
        uint b = 0;
        while (!data.IsEmpty)
        {
            // 5552 bytes is the most that can be summed before b can overflow.
            var chunk = data.Length > 5552 ? data[..5552] : data;
            foreach (var value in chunk)
            {
                a += value;
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
            data = data[chunk.Length..];
        }

        return (b << 16) | a;
    }
}
