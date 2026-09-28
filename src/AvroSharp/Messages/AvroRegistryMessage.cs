using System;
using System.Buffers;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Serialization;

namespace AvroSharp.Messages;

/// <summary>
/// Writes messages in a schema registry's wire framing (<see cref="AvroRegistryFraming"/>): the framing's header with
/// the schema ID, then the object's Avro binary encoding (zlib-compressed for <see cref="AvroRegistryFraming.AwsGlueCompressed"/>).
/// No registry client is involved: the caller supplies the ID. To read, use <see cref="AvroRegistryMessageReader{T}"/>.
/// </summary>
public static class AvroRegistryMessage
{
    /// <summary>Writes one object as a framed message.</summary>
    /// <typeparam name="T">The type of the object.</typeparam>
    /// <param name="output">The destination.</param>
    /// <param name="framing">The registry's framing.</param>
    /// <param name="id">The schema ID the registry assigned to the writer schema.</param>
    /// <param name="value">The object.</param>
    /// <param name="write">Writes the object in the writer schema's encoding; for a generated type, its static <c>Write</c> method.</param>
    public static void Write<T>(IBufferWriter<byte> output, AvroRegistryFraming framing, AvroSchemaId id, T value, AvroWriteAction<T> write)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(framing);
        ArgumentNullException.ThrowIfNull(write);
        framing.WriteHeader(output.GetSpan(framing.HeaderLength), id);
        output.Advance(framing.HeaderLength);
        if (!framing.Compresses)
        {
            var writer = new AvroWriter(output);
            write(ref writer, value);
            writer.Flush();
            return;
        }

        using var payload = new PooledBufferWriter();
        var payloadWriter = new AvroWriter(payload);
        write(ref payloadWriter, value);
        payloadWriter.Flush();
        Zlib.Compress(payload.WrittenMemory, output);
    }

    /// <summary>Writes one object as a framed message to a new array.</summary>
    /// <typeparam name="T">The type of the object.</typeparam>
    /// <param name="framing">The registry's framing.</param>
    /// <param name="id">The schema ID the registry assigned to the writer schema.</param>
    /// <param name="value">The object.</param>
    /// <param name="write">Writes the object in the writer schema's encoding; for a generated type, its static <c>Write</c> method.</param>
    public static byte[] ToArray<T>(AvroRegistryFraming framing, AvroSchemaId id, T value, AvroWriteAction<T> write)
    {
        using var output = new PooledBufferWriter();
        Write(output, framing, id, value, write);
        return output.ToArray();
    }

    /// <summary>Writes a generic value as a framed message to a new array.</summary>
    /// <param name="framing">The registry's framing.</param>
    /// <param name="id">The schema ID the registry assigned to the writer's schema.</param>
    /// <param name="value">The value.</param>
    /// <param name="writer">The generic writer for the schema.</param>
    public static byte[] ToArray(AvroRegistryFraming framing, AvroSchemaId id, in AvroValue value, GenericDatumWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var copy = value;
        return ToArray(framing, id, copy, (ref w, v) => writer.Write(ref w, v));
    }
}
