using System;

namespace AvroSharp.Streams;

/// <summary>Options for <see cref="AvroStreamReader{T}"/> and <see cref="AvroStreamWriter{T}"/>.</summary>
public sealed class AvroStreamOptions
{
    /// <summary>The default <see cref="BufferSize"/>: 64 KiB.</summary>
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>The default <see cref="MaxDatumLength"/>: 64 MiB.</summary>
    public const int DefaultMaxDatumLength = 64 * 1024 * 1024;

    /// <summary>Gets the default options.</summary>
    public static AvroStreamOptions Default { get; } = new();

    /// <summary>
    /// Gets the size of the buffer between the stream and the objects. The writer writes to the stream once this
    /// many bytes are buffered; the reader reads this much at a time. The default is <see cref="DefaultBufferSize"/>.
    /// </summary>
    public int BufferSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 16);
            field = value;
        }
    } = DefaultBufferSize;

    /// <summary>
    /// Gets the largest object the reader buffers, in bytes. It bounds the memory a stream can make the reader
    /// allocate, whether the object is large or the data is corrupt. The default is <see cref="DefaultMaxDatumLength"/>.
    /// </summary>
    public int MaxDatumLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxDatumLength;

    /// <summary>Gets whether disposing the reader or writer leaves the stream open. The default is <see langword="false"/>.</summary>
    public bool LeaveOpen { get; init; }
}
