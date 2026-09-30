#if NET8_0_OR_GREATER
using System;
using System.Buffers;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization.Generated;

namespace AvroSharp.Serialization;

/// <summary>
/// Serializes and deserializes types with generated serializers (<see cref="IAvroSerializable{TSelf}"/>) in Avro
/// binary encoding, without delegates or reflection.
/// </summary>
public static class AvroSerializer
{
    /// <summary>Writes a value to a new array.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="value">The value.</param>
    public static byte[] Serialize<T>(T value)
        where T : IAvroSerializable<T> =>
        AvroGeneratedCode.SerializeToArray(value, AvroSerializable<T>.Write);

    /// <summary>Writes a value to <paramref name="output"/>, allocating nothing for a reused buffer writer.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="output">The destination.</param>
    /// <param name="value">The value.</param>
    public static void Serialize<T>(IBufferWriter<byte> output, T value)
        where T : IAvroSerializable<T>
    {
        var writer = new AvroWriter(output);
        T.Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Writes a value into <paramref name="destination"/>, allocating nothing.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="destination">The caller's memory.</param>
    /// <param name="value">The value.</param>
    /// <param name="bytesWritten">The bytes written, or 0 when the value does not fit.</param>
    /// <returns><see langword="true"/> when the value fit.</returns>
    public static bool TrySerialize<T>(Span<byte> destination, T value, out int bytesWritten)
        where T : IAvroSerializable<T>
    {
        var writer = AvroGeneratedCode.BeginTryWrite(destination);
        T.Write(ref writer, value);
        return AvroGeneratedCode.EndTryWrite(ref writer, out bytesWritten);
    }

    /// <summary>Reads a value written with the type's schema.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="data">The data.</param>
    public static T Deserialize<T>(ReadOnlySpan<byte> data)
        where T : IAvroSerializable<T>
    {
        var reader = new AvroReader(data);
        return T.Read(ref reader);
    }

    /// <summary>Reads a value written with the type's schema from a sequence of buffers.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="data">The data.</param>
    public static T Deserialize<T>(in ReadOnlySequence<byte> data)
        where T : IAvroSerializable<T>
    {
        var reader = new AvroReader(data);
        return T.Read(ref reader);
    }

    /// <summary>Reads a value written with <paramref name="writerSchema"/>, another version of the type's schema.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="writerSchema">The schema the data was written with.</param>
    public static T Deserialize<T>(ReadOnlySpan<byte> data, AvroSchema writerSchema)
        where T : IAvroSerializable<T>
    {
        var reader = new AvroReader(data);
        return T.Read(ref reader, writerSchema);
    }

    /// <summary>Reads a value written with <paramref name="writerSchema"/>, another version of the type's schema, from a sequence of buffers.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="writerSchema">The schema the data was written with.</param>
    public static T Deserialize<T>(in ReadOnlySequence<byte> data, AvroSchema writerSchema)
        where T : IAvroSerializable<T>
    {
        var reader = new AvroReader(data);
        return T.Read(ref reader, writerSchema);
    }
}

/// <summary>The delegates of a generated type's serializers, created once, for the APIs that take delegates.</summary>
internal static class AvroSerializable<T>
    where T : IAvroSerializable<T>
{
    public static readonly AvroWriteAction<T> Write = static (ref writer, value) => T.Write(ref writer, value);

    public static readonly AvroReadFunc<T> Read = static (ref reader) => T.Read(ref reader);

    /// <summary>The reader of data written with <paramref name="writerSchema"/>: the type's own when the schemas match.</summary>
    public static AvroReadFunc<T> For(AvroSchema writerSchema) =>
        writerSchema.HasSameCanonicalForm(T.Schema) ? Read : (ref reader) => T.Read(ref reader, writerSchema);
}
#endif
