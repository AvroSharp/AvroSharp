using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AvroSharp.Generators;

/// <summary>
/// The result of one <c>[AvroSerializable]</c> type, without Roslyn symbols, so the incremental pipeline can cache it:
/// the generated source, or none when there were errors, and the diagnostics.
/// </summary>
internal sealed class SerializableTypeModel(string hintName, string? source, EquatableArray<DiagnosticInfo> diagnostics) : IEquatable<SerializableTypeModel>
{
    /// <summary>Gets the name of the generated file.</summary>
    public string HintName { get; } = hintName;

    /// <summary>Gets the generated source, or <see langword="null"/> when the type has errors.</summary>
    public string? Source { get; } = source;

    /// <summary>Gets the problems found, reported at the code.</summary>
    public EquatableArray<DiagnosticInfo> Diagnostics { get; } = diagnostics;

    public bool Equals(SerializableTypeModel? other) =>
        other is not null
        && string.Equals(HintName, other.HintName, StringComparison.Ordinal)
        && string.Equals(Source, other.Source, StringComparison.Ordinal)
        && Diagnostics.Equals(other.Diagnostics);

    public override bool Equals(object? obj) => obj is SerializableTypeModel other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(HintName);
}

/// <summary>A diagnostic without Roslyn objects: its descriptor's ID, its message and where it is.</summary>
internal sealed class DiagnosticInfo(string id, string message, string? path, TextSpan span, LinePositionSpan lines) : IEquatable<DiagnosticInfo>
{
    public string Id { get; } = id;

    public string Message { get; } = message;

    public string? Path { get; } = path;

    public TextSpan Span { get; } = span;

    public LinePositionSpan Lines { get; } = lines;

    public static DiagnosticInfo At(string id, string message, Location? location)
    {
        if (location is { IsInSource: true })
        {
            var lines = location.GetLineSpan();
            return new(id, message, lines.Path, location.SourceSpan, lines.Span);
        }

        return new(id, message, null, default, default);
    }

    public Location ToLocation() => Path is null ? Location.None : Location.Create(Path, Span, Lines);

    public bool Equals(DiagnosticInfo? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Message, other.Message, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && Span == other.Span;

    public override bool Equals(object? obj) => obj is DiagnosticInfo other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Message);
}
