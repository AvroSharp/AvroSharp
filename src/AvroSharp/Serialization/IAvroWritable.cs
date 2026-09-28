using AvroSharp.IO;

namespace AvroSharp.Serialization;

/// <summary>A value that writes itself in Avro binary encoding; every generated record implements it.</summary>
public interface IAvroWritable
{
    /// <summary>Writes this value.</summary>
    /// <param name="writer">The destination.</param>
    void WriteTo(ref AvroWriter writer);
}

/// <summary>
/// A value that reads Avro binary data into itself, so one instance can be reused for a sequence of values; every
/// generated record implements it.
/// </summary>
public interface IAvroReadable
{
    /// <summary>
    /// Reads a value written with this type's schema into this instance, replacing every field. The instance's
    /// lists, dictionaries and nested records are cleared and filled again rather than replaced.
    /// </summary>
    /// <param name="reader">The source.</param>
    void ReadFrom(ref AvroReader reader);
}
