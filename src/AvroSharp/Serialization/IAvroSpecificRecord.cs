using AvroSharp.Schemas;

namespace AvroSharp.Serialization;

/// <summary>
/// Field access by position, implemented by every record type the AvroSharp source generator emits. It follows the
/// contract of Apache.Avro's <c>ISpecificRecord</c> (<c>Schema</c>, <c>Get</c>, <c>Put</c>) without depending on it.
/// </summary>
/// <remarks>
/// Values are boxed. Generated serializers do not use this interface; it is for tools that work with any record by
/// field position, and for moving values between generated types and other representations.
/// </remarks>
public interface IAvroSpecificRecord
{
    /// <summary>Gets the record's schema.</summary>
    RecordSchema Schema { get; }

    /// <summary>Gets the value of the field at <paramref name="fieldPos"/>.</summary>
    /// <param name="fieldPos">The field's position in the schema.</param>
    /// <exception cref="AvroException">The record has no field at that position.</exception>
    object? Get(int fieldPos);

    /// <summary>Sets the value of the field at <paramref name="fieldPos"/>.</summary>
    /// <param name="fieldPos">The field's position in the schema.</param>
    /// <param name="fieldValue">A value of the field's C# type; <see langword="null"/> only when the field is nullable.</param>
    /// <exception cref="AvroException">The record has no field at that position, or the value's type does not match the field.</exception>
    void Put(int fieldPos, object? fieldValue);
}
