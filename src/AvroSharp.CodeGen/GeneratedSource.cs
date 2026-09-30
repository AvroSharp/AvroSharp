using System;
using System.Collections.Generic;
using System.Linq;

namespace AvroSharp.CodeGen;

/// <summary>One generated C# source file.</summary>
public sealed class GeneratedSource : IEquatable<GeneratedSource>
{
    /// <summary>Initializes a new instance of the <see cref="GeneratedSource"/> class.</summary>
    /// <param name="hintName">The file name, unique within one generation run.</param>
    /// <param name="text">The C# source.</param>
    public GeneratedSource(string hintName, string text)
        : this(hintName, text, [])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GeneratedSource"/> class, with notes about the generated code.</summary>
    /// <param name="hintName">The file name, unique within one generation run.</param>
    /// <param name="text">The C# source.</param>
    /// <param name="notes">Things a user may not expect, such as a property renamed to avoid a clash.</param>
    public GeneratedSource(string hintName, string text, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(hintName);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(notes);
        HintName = hintName;
        Text = text;
        Notes = notes;
    }

    /// <summary>Gets the file name, for example <c>com.example.User.g.cs</c>.</summary>
    public string HintName { get; }

    /// <summary>Gets the C# source.</summary>
    public string Text { get; }

    /// <summary>
    /// Gets notes about the generated code that a user may not expect, such as a property renamed to avoid a clash.
    /// The source generator reports each as an informational diagnostic.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// Gets the C# namespace of the generated type, after <see cref="CodeGenOptions.Namespace"/> and
    /// <see cref="CodeGenOptions.NamespaceMap"/>, or <see langword="null"/> for the global namespace; for example,
    /// to place the file in a folder for it.
    /// </summary>
    public string? Namespace { get; init; }

    /// <inheritdoc />
    public bool Equals(GeneratedSource? other) =>
        other is not null
        && string.Equals(HintName, other.HintName, StringComparison.Ordinal)
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
        && Notes.SequenceEqual(other.Notes, StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GeneratedSource other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(HintName);

    /// <inheritdoc />
    public override string ToString() => HintName;
}
