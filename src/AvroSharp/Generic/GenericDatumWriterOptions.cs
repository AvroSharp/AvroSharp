namespace AvroSharp.Generic;

/// <summary>Limits for <see cref="GenericDatumWriter"/>.</summary>
public sealed class GenericDatumWriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static GenericDatumWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the deepest nesting of records allowed while writing one value. A deeper value, including a record that
    /// contains itself, is rejected with <see cref="AvroException"/> instead of overflowing the stack. Defaults to 128.
    /// </summary>
    public int MaxDepth { get; init; } = 128;
}