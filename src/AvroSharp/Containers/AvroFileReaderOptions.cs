using System;
using System.Collections.Generic;

namespace AvroSharp.Containers;

/// <summary>Options for <see cref="AvroFileReader{T}"/>.</summary>
public sealed class AvroFileReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static AvroFileReaderOptions Default { get; } = new();

    /// <summary>The default <see cref="MaxBlockLength"/>: 64 MiB.</summary>
    public const int DefaultMaxBlockLength = 64 * 1024 * 1024;

    /// <summary>
    /// Gets the codecs available besides the built-in null and deflate codecs, matched by <see cref="AvroCodec.Name"/>
    /// against the file's <c>avro.codec</c> entry. A codec added here replaces a built-in one of the same name.
    /// </summary>
    public IReadOnlyList<AvroCodec> Codecs { get; init; } = [];

    /// <summary>
    /// Gets the largest block, compressed or decompressed, and the largest header metadata value, in bytes.
    /// It bounds the memory a file can make the reader allocate. The default is <see cref="DefaultMaxBlockLength"/>.
    /// </summary>
    public int MaxBlockLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxBlockLength;

    /// <summary>Gets whether disposing the reader leaves the stream open. The default is <see langword="false"/>.</summary>
    public bool LeaveOpen { get; init; }
}
