using System;

namespace AvroSharp.CodeGen;

/// <summary>One generated C# source file.</summary>
public sealed class GeneratedSource : IEquatable<GeneratedSource>
{
    /// <summary>Initializes a new instance of the <see cref="GeneratedSource"/> class.</summary>
    /// <param name="hintName">The file name, unique within one generation run.</param>
    /// <param name="text">The C# source.</param>
    public GeneratedSource(string hintName, string text)
    {
        ArgumentNullException.ThrowIfNull(hintName);
        ArgumentNullException.ThrowIfNull(text);
        HintName = hintName;
        Text = text;
    }

    /// <summary>Gets the file name, for example <c>com.example.User.g.cs</c>.</summary>
    public string HintName { get; }

    /// <summary>Gets the C# source.</summary>
    public string Text { get; }

    /// <inheritdoc />
    public bool Equals(GeneratedSource? other) =>
        other is not null
        && string.Equals(HintName, other.HintName, StringComparison.Ordinal)
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GeneratedSource other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(HintName);

    /// <inheritdoc />
    public override string ToString() => HintName;
}
