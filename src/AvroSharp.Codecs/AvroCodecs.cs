using System.Collections.Generic;
using AvroSharp.Containers;

namespace AvroSharp.Codecs;

/// <summary>The codecs of this package, for readers of files whose codec is not known in advance.</summary>
/// <example>
/// <code>
/// using var reader = AvroFileReader.OpenGeneric(stream, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
/// </code>
/// </example>
public static class AvroCodecs
{
    /// <summary>
    /// Gets the snappy, zstandard, bzip2 and xz codecs with their default settings. With the built-in null and deflate
    /// codecs, a reader given these reads every codec in the specification.
    /// </summary>
    public static IReadOnlyList<AvroCodec> All { get; } = [SnappyCodec.Default, ZstandardCodec.Default, Bzip2Codec.Default, XzCodec.Default];
}
