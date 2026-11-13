using System;
using System.Text.Json;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Serialization.Generated;

/// <summary>
/// The Avro JSON encoding for generated types (#19). A value goes through the binary encoding and the generic model, so
/// its JSON is exactly <see cref="GenericDatumJsonWriter"/>'s and <see cref="GenericDatumJsonReader"/> reads it the same
/// way: wrapped unions, byte strings, NaN as a string, fields in any order and defaults for missing fields.
/// </summary>
public static partial class AvroGeneratedCode
{
    /// <summary>Writes a value in the Avro JSON encoding.</summary>
    /// <typeparam name="T">The generated type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="write">The generated write method.</param>
    /// <param name="schema">The type's schema.</param>
    /// <param name="indented">Whether to indent the JSON.</param>
    public static string SerializeToJson<T>(T value, AvroWriteAction<T> write, AvroSchema schema, bool indented) =>
        GenericDatumJsonWriter.Create(schema).WriteToString(ToGeneric(value, write, schema), indented);

    /// <summary>Writes a value in the Avro JSON encoding to a <see cref="Utf8JsonWriter"/>, as one JSON value.</summary>
    /// <typeparam name="T">The generated type.</typeparam>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="write">The generated write method.</param>
    /// <param name="schema">The type's schema.</param>
    public static void WriteJson<T>(Utf8JsonWriter writer, T value, AvroWriteAction<T> write, AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(writer);
        GenericDatumJsonWriter.Create(schema).Write(writer, ToGeneric(value, write, schema));
    }

    /// <summary>Reads a value from the Avro JSON encoding.</summary>
    /// <typeparam name="T">The generated type.</typeparam>
    /// <param name="json">The JSON text.</param>
    /// <param name="schema">The type's schema.</param>
    /// <param name="read">The generated read method.</param>
    /// <exception cref="AvroDataException">The JSON is malformed or doesn't match the schema.</exception>
    public static T DeserializeFromJson<T>(string json, AvroSchema schema, AvroReadFunc<T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromGeneric(GenericDatumJsonReader.Create(schema).Read(json), schema, read);
    }

    /// <summary>Reads a value from the Avro JSON encoding, as UTF-8.</summary>
    /// <typeparam name="T">The generated type.</typeparam>
    /// <param name="utf8Json">The JSON text.</param>
    /// <param name="schema">The type's schema.</param>
    /// <param name="read">The generated read method.</param>
    /// <exception cref="AvroDataException">The JSON is malformed or doesn't match the schema.</exception>
    public static T DeserializeFromJson<T>(ReadOnlySpan<byte> utf8Json, AvroSchema schema, AvroReadFunc<T> read) =>
        FromGeneric(GenericDatumJsonReader.Create(schema).Read(utf8Json), schema, read);

    private static AvroValue ToGeneric<T>(T value, AvroWriteAction<T> write, AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return GenericDatumReader.Create(schema).Read(SerializeToArray(value, write));
    }

    private static T FromGeneric<T>(in AvroValue value, AvroSchema schema, AvroReadFunc<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var bytes = GenericDatumWriter.Create(schema).WriteToArray(value);
        var reader = new IO.AvroReader(bytes);
        return read(ref reader);
    }
}
