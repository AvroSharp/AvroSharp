using System;

namespace AvroSharp.Generic;

/// <summary>The shape of the values <see cref="AvroRandomData"/> generates.</summary>
public sealed class AvroRandomDataOptions
{
    /// <summary>Gets the default options.</summary>
    public static AvroRandomDataOptions Default { get; } = new();

    /// <summary>Gets the largest number of items in an array or entries in a map, zero or more. The default is 5.</summary>
    public int MaxCollectionLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 5;

    /// <summary>
    /// Gets the longest string, in UTF-16 code units, and the longest <c>bytes</c> value, in bytes, zero or more. Map
    /// keys are at most 8 long, or this when it is shorter. The default is 30.
    /// </summary>
    public int MaxStringLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 30;

    /// <summary>
    /// Gets how often a union with a <c>null</c> branch takes it, from 0 to 1, or <see langword="null"/> (the default)
    /// for as often as each other branch. Unions without a <c>null</c> branch pick each branch equally often.
    /// </summary>
    public double? NullBranchProbability
    {
        get;
        init
        {
            if (value is { } probability && !(probability >= 0 && probability <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The probability must be from 0 to 1.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets whether logical values stay in the range of the .NET type generated code maps them to: <c>date</c> in
    /// <see cref="DateTime"/>'s years 1 to 9999, and the <c>timestamp-millis</c>, <c>timestamp-micros</c> and local
    /// timestamps in that range too. The default is <see langword="true"/>. When <see langword="false"/>, those take
    /// any value of their underlying type. Every other logical value is always valid: decimals fit their precision,
    /// times are within a day, and UUIDs are well formed.
    /// </summary>
    public bool LogicalValuesInDotNetRange { get; init; } = true;

    /// <summary>
    /// Gets how deep records nest before the generator ends the recursion, at least 1: below it, arrays and maps are
    /// empty and unions take the branch that ends soonest. The default is 6.
    /// </summary>
    public int MaxDepth
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 6;
}
