namespace AvroSharp.Generic;

/// <summary>Limits that protect <see cref="GenericDatumReader"/> against malformed or hostile input.</summary>
public sealed class GenericDatumReaderOptions
{
    // The defaults as constants, so readers with the default options build their state from immediates.
    internal const int DefaultMaxDepth = 128;
    internal const int DefaultMaxZeroSizeItems = 1 << 16;

    /// <summary>Gets the default options.</summary>
    public static GenericDatumReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets the deepest nesting of records allowed while reading one value. Deeper input is rejected with
    /// <see cref="AvroDataException"/> instead of overflowing the stack. Defaults to 128.
    /// </summary>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>
    /// Gets the largest total number of zero-size array items (for example <c>null</c>s or empty records) allowed
    /// while reading one value. Such items take no input bytes, so the input size cannot bound them. Defaults to 65,536.
    /// </summary>
    public int MaxZeroSizeItems { get; init; } = DefaultMaxZeroSizeItems;
}
