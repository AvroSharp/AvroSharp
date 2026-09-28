#if NET8_0_OR_GREATER
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Serialization;

/// <summary>
/// A type with generated Avro serializers, as static members, so generic code can read and write it without delegates
/// or reflection (see <see cref="AvroSerializer"/>). Every record the AvroSharp source generator emits implements it
/// on .NET 8 and later.
/// </summary>
/// <typeparam name="TSelf">The type itself.</typeparam>
public interface IAvroSerializable<TSelf>
    where TSelf : IAvroSerializable<TSelf>
{
    /// <summary>Gets the type's Avro schema.</summary>
    static abstract AvroSchema Schema { get; }

    /// <summary>Writes a value.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    static abstract void Write(ref AvroWriter writer, TSelf value);

    /// <summary>Reads a value written with the type's schema.</summary>
    /// <param name="reader">The source.</param>
    static abstract TSelf Read(ref AvroReader reader);

    /// <summary>Reads a value written with <paramref name="writerSchema"/>, another version of the type's schema.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="writerSchema">The schema the data was written with.</param>
    static abstract TSelf Read(ref AvroReader reader, AvroSchema writerSchema);
}
#endif
