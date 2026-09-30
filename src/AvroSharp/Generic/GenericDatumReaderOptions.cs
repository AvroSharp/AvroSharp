using System;

namespace AvroSharp.Generic;

/// <summary>Limits that protect <see cref="GenericDatumReader"/> against malformed or hostile input.</summary>
/// <seealso cref="GenericDatumJsonReader"/>
public sealed class GenericDatumReaderOptions
{
    // The defaults are constants, so readers with the default options build their state from immediates.

    /// <summary>The default <see cref="MaxDepth"/>: 128.</summary>
    public const int DefaultMaxDepth = 128;

    /// <summary>The default <see cref="MaxZeroSizeItems"/>: 65,536.</summary>
    public const int DefaultMaxZeroSizeItems = 1 << 16;

    /// <summary>Gets the default options.</summary>
    public static GenericDatumReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets the deepest nesting of records allowed while reading one value, at least 1. Deeper input is rejected with
    /// <see cref="AvroDataException"/> instead of overflowing the stack. The default is <see cref="DefaultMaxDepth"/>.
    /// </summary>
    public int MaxDepth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultMaxDepth;

    /// <summary>
    /// Gets the largest total number of zero-size array items (for example <c>null</c>s or empty records) allowed
    /// while reading one value, zero or more. Such items take no input bytes, so the input size cannot bound them. The
    /// default is <see cref="DefaultMaxZeroSizeItems"/>.
    /// </summary>
    public int MaxZeroSizeItems
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = DefaultMaxZeroSizeItems;
}
