using System;
using System.Collections.Generic;

namespace AvroSharp.Containers;

/// <summary>Options for <see cref="AvroFileWriter{T}"/>.</summary>
public sealed class AvroFileWriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static AvroFileWriterOptions Default { get; } = new();

    /// <summary>The default <see cref="SyncInterval"/>: 64 KiB.</summary>
    public const int DefaultSyncInterval = 64 * 1024;

    /// <summary>Gets the codec that compresses each block. The default is <see cref="AvroCodec.Null"/>.</summary>
    public AvroCodec Codec { get; init; } = AvroCodec.Null;

    /// <summary>
    /// Gets the approximate size of a block before compression: a block is written once its encoded objects
    /// reach this many bytes. The default is <see cref="DefaultSyncInterval"/>.
    /// </summary>
    public int SyncInterval
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultSyncInterval;

    /// <summary>
    /// Gets the application metadata written to the file header. Keys beginning with <c>avro.</c> are reserved by the
    /// specification and rejected; strings are conventionally stored as UTF-8.
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? Metadata { get; init; }

    /// <summary>Gets whether disposing the writer leaves the stream open. The default is <see langword="false"/>.</summary>
    public bool LeaveOpen { get; init; }
}
