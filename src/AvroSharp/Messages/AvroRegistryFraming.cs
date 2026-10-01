using System;
using System.Buffers.Binary;

namespace AvroSharp.Messages;

/// <summary>
/// How a schema registry frames a message: the bytes in front of the Avro binary data that identify its schema.
/// Each registry's layout is a fixed instance; see <see cref="AvroRegistryMessage"/> and
/// <see cref="AvroRegistryMessageReader{T}"/>.
/// </summary>
/// <seealso cref="AvroRegistryMessage"/>
/// <seealso cref="AvroRegistryMessageReader"/>
/// <seealso cref="AvroSchemaId"/>
public sealed class AvroRegistryFraming
{
    private enum Layout
    {
        Int32,
        Int64,
        Guid,
        Glue,
    }

    private readonly Layout _layout;
    private readonly byte _magic;

    private AvroRegistryFraming(string name, Layout layout, byte magic, bool compress)
    {
        Name = name;
        _layout = layout;
        _magic = magic;
        Compresses = compress;
        HeaderLength = layout switch
        {
            Layout.Int32 => 5,
            Layout.Int64 => 9,
            Layout.Guid => 17,
            _ => 18,
        };
    }

    /// <summary>Gets Confluent's framing: the byte <c>0x00</c>, then the schema ID as a 4-byte big-endian integer.</summary>
    public static AvroRegistryFraming Confluent { get; } = new("Confluent", Layout.Int32, 0x00, compress: false);

    /// <summary>
    /// Gets Confluent's version 1 framing (Confluent Platform 8): the byte <c>0x01</c>, then the schema GUID as 16
    /// big-endian bytes (the 8-4-4-4-12 text form, in order).
    /// </summary>
    public static AvroRegistryFraming ConfluentGuid { get; } = new("ConfluentGuid", Layout.Guid, 0x01, compress: false);

    /// <summary>
    /// Gets Apicurio Registry 3's default framing (<c>Default4ByteIdHandler</c>): the byte <c>0x00</c>, then the ID
    /// (the content ID by default) as a 4-byte big-endian integer. It is the same layout as <see cref="Confluent"/>.
    /// </summary>
    public static AvroRegistryFraming Apicurio { get; } = new("Apicurio", Layout.Int32, 0x00, compress: false);

    /// <summary>
    /// Gets Apicurio's 8-byte framing (Apicurio Registry 2's default, <c>Legacy8ByteIdHandler</c> in 3): the byte
    /// <c>0x00</c>, then the ID (the global ID by default in 2) as an 8-byte big-endian integer.
    /// </summary>
    public static AvroRegistryFraming Apicurio8Byte { get; } = new("Apicurio8Byte", Layout.Int64, 0x00, compress: false);

    /// <summary>
    /// Gets AWS Glue Schema Registry's framing, uncompressed: the header version byte <c>0x03</c>, the compression byte
    /// <c>0x00</c>, then the schema version ID as a 16-byte big-endian UUID. Reading also accepts zlib-compressed
    /// data (compression byte <c>0x05</c>).
    /// </summary>
    public static AvroRegistryFraming AwsGlue { get; } = new("AwsGlue", Layout.Glue, 0x03, compress: false);

    /// <summary>
    /// Gets AWS Glue Schema Registry's framing with zlib compression (compression byte <c>0x05</c>): the Avro data
    /// after the header is compressed, as the Glue serializer does when compression is enabled.
    /// </summary>
    public static AvroRegistryFraming AwsGlueCompressed { get; } = new("AwsGlueCompressed", Layout.Glue, 0x03, compress: true);

    /// <summary>Gets the framing's name.</summary>
    public string Name { get; }

    /// <summary>Gets the number of bytes in front of the Avro data.</summary>
    public int HeaderLength { get; }

    /// <summary>Gets whether writing compresses the Avro data (AWS Glue with zlib).</summary>
    public bool Compresses { get; }

    internal const byte GlueNoCompression = 0x00;
    internal const byte GlueZlib = 0x05;

    /// <summary>Writes the header for <paramref name="id"/>.</summary>
    /// <param name="destination">At least <see cref="HeaderLength"/> bytes.</param>
    /// <param name="id">The schema ID: a number for Confluent and Apicurio, a GUID for Confluent version 1 and AWS Glue.</param>
    /// <exception cref="ArgumentException">The ID is of the wrong kind or out of range for this framing.</exception>
    public void WriteHeader(Span<byte> destination, AvroSchemaId id)
    {
        if (destination.Length < HeaderLength)
        {
            throw new ArgumentException($"The destination must hold at least {HeaderLength} bytes.", nameof(destination));
        }

        destination[0] = _magic;
        switch (_layout)
        {
            case Layout.Int32:
                var number = NumberOf(id);
                if (number is < int.MinValue or > int.MaxValue)
                {
                    throw new ArgumentException($"{Name} framing holds a 4-byte ID; {number} does not fit.", nameof(id));
                }

                BinaryPrimitives.WriteInt32BigEndian(destination[1..], (int)number);
                break;
            case Layout.Int64:
                BinaryPrimitives.WriteInt64BigEndian(destination[1..], NumberOf(id));
                break;
            case Layout.Guid:
                WriteGuidBigEndian(destination[1..], GuidOf(id));
                break;
            default:
                destination[1] = Compresses ? GlueZlib : GlueNoCompression;
                WriteGuidBigEndian(destination[2..], GuidOf(id));
                break;
        }
    }

    /// <summary>Reads the header of a message in this framing.</summary>
    /// <param name="message">The message.</param>
    /// <param name="id">The schema ID.</param>
    /// <returns><see langword="false"/> when the message is shorter than the header or starts with another magic byte.</returns>
    public bool TryReadHeader(ReadOnlySpan<byte> message, out AvroSchemaId id) => TryReadHeader(message, out id, out _);

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal bool TryReadHeader(ReadOnlySpan<byte> message, out AvroSchemaId id, out bool compressed)
    {
        id = default;
        compressed = false;
        if (message.Length < HeaderLength || message[0] != _magic)
        {
            return false;
        }

        switch (_layout)
        {
            case Layout.Int32:
                id = AvroSchemaId.FromNumber(BinaryPrimitives.ReadInt32BigEndian(message[1..]));
                return true;
            case Layout.Int64:
                id = AvroSchemaId.FromNumber(BinaryPrimitives.ReadInt64BigEndian(message[1..]));
                return true;
            case Layout.Guid:
                id = AvroSchemaId.FromGuid(ReadGuidBigEndian(message[1..]));
                return true;
            default:
                if (message[1] is not (GlueNoCompression or GlueZlib))
                {
                    return false;
                }

                compressed = message[1] == GlueZlib;
                id = AvroSchemaId.FromGuid(ReadGuidBigEndian(message[2..]));
                return true;
        }
    }

    private long NumberOf(AvroSchemaId id) =>
        !id.IsGuid ? id.Number : throw new ArgumentException($"{Name} framing holds a numeric schema ID, not a GUID.", nameof(id));

    private Guid GuidOf(AvroSchemaId id) =>
        id.IsGuid ? id.Guid : throw new ArgumentException($"{Name} framing holds a GUID schema ID, not a number.", nameof(id));

    // Java's UUID byte order (most significant long, then least, each big-endian), which is the order of the text form.
    internal static void WriteGuidBigEndian(Span<byte> destination, Guid guid)
    {
        var bytes = guid.ToByteArray(); // the first three groups little-endian
        destination[0] = bytes[3];
        destination[1] = bytes[2];
        destination[2] = bytes[1];
        destination[3] = bytes[0];
        destination[4] = bytes[5];
        destination[5] = bytes[4];
        destination[6] = bytes[7];
        destination[7] = bytes[6];
        bytes.AsSpan(8, 8).CopyTo(destination[8..]);
    }

    internal static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
        var bytes = new byte[16];
        bytes[0] = source[3];
        bytes[1] = source[2];
        bytes[2] = source[1];
        bytes[3] = source[0];
        bytes[4] = source[5];
        bytes[5] = source[4];
        bytes[6] = source[7];
        bytes[7] = source[6];
        source.Slice(8, 8).CopyTo(bytes.AsSpan(8));
        return new Guid(bytes);
    }
}
