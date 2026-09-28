using System;
using System.Buffers;
using System.Buffers.Binary;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Messages;

/// <summary>
/// Avro single-object encoding: the marker bytes <c>C3 01</c>, the writer schema's 8-byte little-endian CRC-64-AVRO
/// fingerprint, then the object's binary encoding. A reader looks the fingerprint up in an
/// <see cref="IAvroSchemaStore"/> to find the writer schema (see <see cref="AvroMessageReader{T}"/>).
/// </summary>
public static class AvroMessage
{
    /// <summary>The length of the header: two marker bytes and the 8-byte fingerprint.</summary>
    public const int HeaderLength = 10;

    private const byte Marker0 = 0xC3;
    private const byte Marker1 = 0x01;

    /// <summary>Writes the header for <paramref name="schema"/>.</summary>
    /// <param name="destination">At least <see cref="HeaderLength"/> bytes.</param>
    /// <param name="schema">The writer schema.</param>
    public static void WriteHeader(Span<byte> destination, AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (destination.Length < HeaderLength)
        {
            throw new ArgumentException($"The destination must hold at least {HeaderLength} bytes.", nameof(destination));
        }

        destination[0] = Marker0;
        destination[1] = Marker1;
        BinaryPrimitives.WriteInt64LittleEndian(destination[2..], schema.Fingerprint64);
    }

    /// <summary>Reads the header of a single-object encoded message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fingerprint">The writer schema's CRC-64-AVRO fingerprint (<see cref="AvroSchema.Fingerprint64"/>).</param>
    /// <returns><see langword="false"/> when the data is too short or does not start with the marker bytes.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> message, out long fingerprint)
    {
        if (message.Length < HeaderLength || message[0] != Marker0 || message[1] != Marker1)
        {
            fingerprint = 0;
            return false;
        }

        fingerprint = BinaryPrimitives.ReadInt64LittleEndian(message[2..]);
        return true;
    }

    /// <summary>Writes one object as a single-object encoded message.</summary>
    /// <typeparam name="T">The type of the object.</typeparam>
    /// <param name="output">The destination.</param>
    /// <param name="value">The object.</param>
    /// <param name="schema">The schema the object is written with; its fingerprint goes into the header.</param>
    /// <param name="write">Writes the object in <paramref name="schema"/>'s encoding; for a generated type, its static <c>Write</c> method.</param>
    public static void Write<T>(IBufferWriter<byte> output, T value, AvroSchema schema, AvroWriteAction<T> write)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(write);
        WriteHeader(output.GetSpan(HeaderLength), schema);
        output.Advance(HeaderLength);
        var writer = new AvroWriter(output);
        write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Writes one object as a single-object encoded message to a new array.</summary>
    /// <typeparam name="T">The type of the object.</typeparam>
    /// <param name="value">The object.</param>
    /// <param name="schema">The schema the object is written with; its fingerprint goes into the header.</param>
    /// <param name="write">Writes the object in <paramref name="schema"/>'s encoding; for a generated type, its static <c>Write</c> method.</param>
    public static byte[] ToArray<T>(T value, AvroSchema schema, AvroWriteAction<T> write)
    {
        using var output = new PooledBufferWriter();
        Write(output, value, schema, write);
        return output.ToArray();
    }

    /// <summary>Writes a generic value as a single-object encoded message to a new array.</summary>
    /// <param name="value">The value.</param>
    /// <param name="writer">The generic writer; its schema's fingerprint goes into the header.</param>
    public static byte[] ToArray(in AvroValue value, GenericDatumWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var output = new PooledBufferWriter();
        WriteHeader(output.GetSpan(HeaderLength), writer.Schema);
        output.Advance(HeaderLength);
        writer.Write(output, value);
        return output.ToArray();
    }

#if NET8_0_OR_GREATER
    /// <summary>Writes a single-object message of a generated type, with its schema's fingerprint.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="output">The destination.</param>
    /// <param name="value">The value.</param>
    public static void Write<T>(IBufferWriter<byte> output, T value)
        where T : IAvroSerializable<T> =>
        Write(output, value, T.Schema, AvroSerializable<T>.Write);

    /// <summary>Writes a single-object message of a generated type to a new array.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="value">The value.</param>
    public static byte[] ToArray<T>(T value)
        where T : IAvroSerializable<T> =>
        ToArray(value, T.Schema, AvroSerializable<T>.Write);
#endif
}
