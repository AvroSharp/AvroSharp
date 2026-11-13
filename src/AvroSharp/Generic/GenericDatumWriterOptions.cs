using System;

namespace AvroSharp.Generic;

/// <summary>Limits for <see cref="GenericDatumWriter"/> and <see cref="GenericDatumJsonWriter"/>, and the JSON writer's labels.</summary>
/// <seealso cref="GenericDatumJsonWriter"/>
public sealed class GenericDatumWriterOptions
{
    /// <summary>The default <see cref="MaxDepth"/>: 128.</summary>
    public const int DefaultMaxDepth = 128;

    /// <summary>Gets the default options.</summary>
    public static GenericDatumWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the deepest nesting of records allowed while writing one value, at least 1. A deeper value, including a
    /// record that contains itself, is rejected with <see cref="AvroException"/> instead of overflowing the stack. The
    /// default is <see cref="DefaultMaxDepth"/>.
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
    /// Gets how <see cref="GenericDatumJsonWriter"/> labels the branch of a union value that has a named type. The
    /// default is <see cref="JsonUnionBranchNames.Full"/>, as the specification says. The binary writer ignores it.
    /// </summary>
    public JsonUnionBranchNames JsonUnionBranchNames
    {
        get;
        init
        {
            if (value is not (JsonUnionBranchNames.Full or JsonUnionBranchNames.Simple))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a JsonUnionBranchNames value.");
            }

            field = value;
        }
    }
}
