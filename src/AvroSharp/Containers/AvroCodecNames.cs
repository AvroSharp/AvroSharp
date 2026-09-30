namespace AvroSharp.Containers;

/// <summary>
/// The codec names registered by the Avro specification for the <c>avro.codec</c>
/// metadata entry of an object container file.
/// </summary>
/// <seealso cref="AvroCodec"/>
public static class AvroCodecNames
{
    /// <summary>Blocks are stored uncompressed.</summary>
    public const string Null = "null";

    /// <summary>Blocks are compressed with raw DEFLATE (RFC 1951), without zlib framing.</summary>
    public const string Deflate = "deflate";

    /// <summary>Blocks are compressed with Snappy and followed by a 4-byte big-endian CRC-32 of the uncompressed data.</summary>
    public const string Snappy = "snappy";

    /// <summary>Blocks are compressed with bzip2.</summary>
    public const string Bzip2 = "bzip2";

    /// <summary>Blocks are compressed with the XZ format.</summary>
    public const string Xz = "xz";

    /// <summary>Blocks are compressed with Zstandard.</summary>
    public const string Zstandard = "zstandard";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is one of the codec names
    /// defined by the specification. Names are case-sensitive.
    /// </summary>
    /// <param name="name">The value of the <c>avro.codec</c> metadata entry.</param>
    public static bool IsStandard(string? name) => name switch
    {
        Null or Deflate or Snappy or Bzip2 or Xz or Zstandard => true,
        _ => false,
    };
}
