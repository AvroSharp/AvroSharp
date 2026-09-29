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

    /// <summary>The default <see cref="MaxSchemaLength"/>: 4 MiB.</summary>
    public const int DefaultMaxSchemaLength = 4 * 1024 * 1024;

    /// <summary>
    /// Gets the largest <c>avro.schema</c> header entry, in bytes. Parsing a schema allocates about 20 times its JSON,
    /// so this bounds what a file's header can make the reader allocate, below <see cref="MaxBlockLength"/>. The
    /// default is <see cref="DefaultMaxSchemaLength"/>.
    /// </summary>
    public int MaxSchemaLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxSchemaLength;

    /// <summary>The default <see cref="MaxZeroSizeValuesPerBlock"/>: 16,777,216.</summary>
    public const long DefaultMaxZeroSizeValuesPerBlock = 1 << 24;

    /// <summary>
    /// Gets how many zero-size values a block may declare, when the file's schema lets an object encode to no bytes
    /// (<c>null</c>, or records of such fields). The input cannot bound those objects, so a few bytes could declare
    /// any number of them. Each object counts as the values reading it creates: one, plus one per field of each
    /// record in it. The default is <see cref="DefaultMaxZeroSizeValuesPerBlock"/>; <see cref="AvroFileWriter{T}"/>
    /// writes at most 65,536 objects per block.
    /// </summary>
    public long MaxZeroSizeValuesPerBlock
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxZeroSizeValuesPerBlock;

    /// <summary>Gets whether disposing the reader leaves the stream open. The default is <see langword="false"/>.</summary>
    public bool LeaveOpen { get; init; }
}
