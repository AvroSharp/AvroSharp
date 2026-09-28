using System.ComponentModel;
using AvroSharp.IO;

namespace AvroSharp.Serialization;

/// <summary>
/// Reads and writes one value of a type, for the collection and union helpers of <see cref="AvroGeneratedCode"/>.
/// Implementations are structs, so a helper instantiated with one is specialized by the JIT and calls it directly:
/// generated code gets one helper call per field instead of an inlined loop, with no delegate or virtual call per item.
/// </summary>
/// <typeparam name="T">The type of the values.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IAvroCodec<T>
{
    /// <summary>Reads a value.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="depth">The nesting depth of records, checked by record codecs.</param>
    T Read(ref AvroReader reader, int depth);

    /// <summary>Writes a value, which is not <see langword="null"/>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="depth">The nesting depth of records, checked by record codecs.</param>
    void Write(ref AvroWriter writer, T value, int depth);
}
