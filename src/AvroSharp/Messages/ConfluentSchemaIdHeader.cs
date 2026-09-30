using System;

namespace AvroSharp.Messages;

/// <summary>
/// Confluent's schema ID in a Kafka message header (Confluent Platform 8, <c>HeaderSchemaIdSerializer</c>): the header
/// value is the byte <c>0x01</c> and the schema GUID as 16 big-endian bytes, and the message value is the Avro data
/// alone. Read the value with <see cref="AvroRegistryMessageReader{T}.ReadPayload"/>.
/// </summary>
/// <seealso cref="AvroRegistryFraming.ConfluentGuid"/>
/// <seealso cref="AvroSchemaId"/>
public static class ConfluentSchemaIdHeader
{
    /// <summary>The header that carries a message key's schema ID.</summary>
    public const string KeyHeaderName = "__key_schema_id";

    /// <summary>The header that carries a message value's schema ID.</summary>
    public const string ValueHeaderName = "__value_schema_id";

    /// <summary>The length of the header value.</summary>
    public const int Length = 17;

    /// <summary>Encodes a schema ID as a header value.</summary>
    /// <param name="schemaId">The schema ID, a GUID (<see cref="AvroSchemaId.FromGuid"/>).</param>
    /// <exception cref="ArgumentException">The ID is a number: this header carries GUIDs.</exception>
    public static byte[] Encode(AvroSchemaId schemaId)
    {
        if (!schemaId.IsGuid)
        {
            throw new ArgumentException("Confluent's schema ID header carries a GUID; the ID is a number.", nameof(schemaId));
        }

        var value = new byte[Length];
        AvroRegistryFraming.ConfluentGuid.WriteHeader(value, schemaId);
        return value;
    }

    /// <summary>Decodes a header value.</summary>
    /// <param name="headerValue">The header value.</param>
    /// <param name="schemaId">The schema ID, a GUID.</param>
    /// <returns><see langword="false"/> when the value is not 17 bytes starting with <c>0x01</c>.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> headerValue, out AvroSchemaId schemaId)
    {
        if (headerValue.Length != Length)
        {
            schemaId = default;
            return false;
        }

        return AvroRegistryFraming.ConfluentGuid.TryReadHeader(headerValue, out schemaId);
    }
}
