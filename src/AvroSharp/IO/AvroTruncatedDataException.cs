namespace AvroSharp.IO;

/// <summary>
/// Raised when the input ends inside a value whose length is known: the input must hold at least
/// <see cref="RequiredLength"/> bytes before the value can be read. <see cref="Streams.AvroStreamReader{T}"/> waits
/// for that many before it decodes an object again (#129).
/// </summary>
#pragma warning disable CA1064, CA1032, RCS1194 // Thrown as AvroDataException; callers never catch this type.
internal sealed class AvroTruncatedDataException(string message, long requiredLength) : AvroDataException(message)
#pragma warning restore CA1064, CA1032, RCS1194
{
    /// <summary>Gets the number of bytes, from the start of the reader's input, that reading the value needs.</summary>
    public long RequiredLength { get; } = requiredLength;
}
