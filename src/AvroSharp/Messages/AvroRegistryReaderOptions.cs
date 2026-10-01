namespace AvroSharp.Messages;

/// <summary>Limits for <see cref="AvroRegistryMessageReader{T}"/>.</summary>
/// <seealso cref="AvroRegistryMessageReader"/>
public sealed class AvroRegistryReaderOptions
{
    /// <summary>The default <see cref="MaxPayloadLength"/>: 64 MiB.</summary>
    public const int DefaultMaxPayloadLength = 64 * 1024 * 1024;

    /// <summary>Gets the default options.</summary>
    public static AvroRegistryReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest payload a compressed message (AWS Glue with zlib) may expand to, in bytes. It bounds the memory
    /// a small message can make the reader allocate. The default is <see cref="DefaultMaxPayloadLength"/>.
    /// </summary>
    public int MaxPayloadLength
    {
        get;
        init
        {
            System.ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxPayloadLength;
}
