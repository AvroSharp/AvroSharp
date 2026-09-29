using AvroSharp.IO;

namespace AvroSharp.Serialization;

/// <summary>Writes one value.</summary>
/// <typeparam name="T">The type written.</typeparam>
/// <param name="writer">The destination.</param>
/// <param name="value">The value.</param>
public delegate void AvroWriteAction<in T>(ref AvroWriter writer, T value);

/// <summary>Reads one value.</summary>
/// <typeparam name="T">The type read.</typeparam>
/// <param name="reader">The source.</param>
public delegate T AvroReadFunc<out T>(ref AvroReader reader);
