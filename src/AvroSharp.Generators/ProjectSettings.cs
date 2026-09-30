using System;
using System.Collections.Generic;
using System.Linq;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AvroSharp.Generators;

/// <summary>
/// The MSBuild properties that configure the generator, as the <see cref="CodeGenOptions"/> they set and the CLI's
/// options name them: <c>AvroSharpNamespace</c> (<c>--namespace</c>), <c>AvroSharpNamespaceMap</c>
/// (<c>--namespace-map</c>, entries separated by <c>;</c>), <c>AvroSharpLogicalTypes</c> (<c>native</c> or <c>raw</c>),
/// <c>AvroSharpPropertyNames</c> (<c>pascal</c> or <c>avro</c>) and <c>AvroSharpApacheCompatible</c> (<c>true</c> or
/// <c>false</c>). A value that is not recognized is reported (AVROGEN006) and the default is used, instead of a typo
/// passing silently. The C# version, nullable annotations and <c>DateOnly</c> come from the project.
/// </summary>
/// <remarks>Compared by value, so the incremental pipeline reuses its output while the properties are unchanged.</remarks>
internal sealed class ProjectSettings : IEquatable<ProjectSettings>
{
    private ProjectSettings(string? ns, EquatableArray<string> namespaceMap, bool raw, bool apache, PropertyNaming? propertyNames, EquatableArray<string> warnings)
    {
        Namespace = ns;
        NamespaceMapEntries = namespaceMap;
        Raw = raw;
        Apache = apache;
        PropertyNames = propertyNames;
        Warnings = warnings;
    }

    public string? Namespace { get; }

    // The valid entries, as avro:csharp, in the order given.
    public EquatableArray<string> NamespaceMapEntries { get; }

    public bool Raw { get; }

    public bool Apache { get; }

    public PropertyNaming? PropertyNames { get; }

    public EquatableArray<string> Warnings { get; }

    public IReadOnlyDictionary<string, string>? NamespaceMap =>
        NamespaceMapEntries.Count == 0
            ? null
            : NamespaceMapEntries.ToDictionary(entry => entry[..entry.IndexOf(':')], entry => entry[(entry.IndexOf(':') + 1)..], StringComparer.Ordinal);

    public static ProjectSettings Read(AnalyzerConfigOptions options)
    {
        var warnings = new List<string>();
        var ns = Get(options, "AvroSharpNamespace");
        var raw = string.Equals(Choice(options, "AvroSharpLogicalTypes", ["native", "raw"], "native", warnings), "raw", StringComparison.Ordinal);
        var apache = string.Equals(Choice(options, "AvroSharpApacheCompatible", ["true", "false"], "false", warnings), "true", StringComparison.Ordinal);
        PropertyNaming? naming = Choice(options, "AvroSharpPropertyNames", ["pascal", "avro"], null, warnings) switch
        {
            "pascal" => PropertyNaming.PascalCase,
            "avro" => PropertyNaming.Avro,
            _ => null,
        };

        return new ProjectSettings(ns, NamespaceMapOf(Get(options, "AvroSharpNamespaceMap"), warnings), raw, apache, naming, new EquatableArray<string>(warnings));
    }

    public bool Equals(ProjectSettings? other) =>
        other is not null
        && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
        && NamespaceMapEntries.Equals(other.NamespaceMapEntries)
        && Raw == other.Raw
        && Apache == other.Apache
        && PropertyNames == other.PropertyNames
        && Warnings.Equals(other.Warnings);

    public override bool Equals(object? obj) => obj is ProjectSettings other && Equals(other);

    public override int GetHashCode() => NamespaceMapEntries.GetHashCode() ^ (Namespace is null ? 0 : StringComparer.Ordinal.GetHashCode(Namespace));

    private static string? Get(AnalyzerConfigOptions options, string property) =>
        options.TryGetValue("build_property." + property, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // The choice the value names (case is ignored); otherwise a warning and the default.
    private static string? Choice(AnalyzerConfigOptions options, string property, string[] choices, string? fallback, List<string> warnings)
    {
        if (Get(options, property) is not { } value)
        {
            return fallback;
        }

        if (choices.FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return chosen;
        }

        var used = fallback is null ? "the default is used" : fallback + " is used";
        warnings.Add($"{property} is '{value}', which is not {string.Join(" or ", choices)}; {used}.");
        return fallback;
    }

    private static EquatableArray<string> NamespaceMapOf(string? value, List<string> warnings)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in (value ?? string.Empty).Split(';').Select(part => part.Trim()).Where(part => part.Length > 0))
        {
            var colon = part.IndexOf(':');
            var avro = colon < 0 ? string.Empty : part[..colon].Trim();
            var csharp = colon < 0 ? string.Empty : part[(colon + 1)..].Trim();
            if (!IsNamespace(avro) || !IsNamespace(csharp))
            {
                warnings.Add($"AvroSharpNamespaceMap has '{part}', which is not avro.namespace:CSharp.Namespace (names separated by dots, on both sides of the colon); it is ignored.");
            }
            else if (!seen.Add(avro))
            {
                warnings.Add($"AvroSharpNamespaceMap maps '{avro}' more than once; the first mapping is used.");
            }
            else
            {
                entries.Add(avro + ":" + csharp);
            }
        }

        return new EquatableArray<string>(entries);
    }

    private static bool IsNamespace(string value) => value.Length > 0 && AvroNames.IsValidNamespace(value.AsSpan());
}
