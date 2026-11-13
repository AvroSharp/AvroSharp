using System;
using System.Collections.Generic;
using AvroSharp.CodeGen;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AvroSharp.Generators;

/// <summary>
/// One schema file's own settings (#18): <c>AdditionalFiles</c> metadata with the names of the MSBuild properties, which
/// overrides the project's for the types the file defines. <c>AvroSharpNamespace</c>, <c>AvroSharpLogicalTypes</c>,
/// <c>AvroSharpPropertyNames</c>, <c>AvroSharpApacheCompatible</c>, <c>AvroSharpRequiredMembers</c> and
/// <c>AvroSharpTypeKind</c> can be set per file; the namespace map, the C# version and nullable annotations are the
/// project's.
/// </summary>
/// <example>
/// <code>
/// &lt;AdditionalFiles Update="Schemas/legacy/*.avsc" AvroSharpNamespace="Acme.Legacy" AvroSharpApacheCompatible="true" /&gt;
/// </code>
/// </example>
/// <remarks>Compared by value, so the incremental pipeline reuses its output while the metadata is unchanged.</remarks>
internal sealed class FileSettings : IEquatable<FileSettings>
{
    private const string Prefix = "build_metadata.AdditionalFiles.";

    private FileSettings(string? ns, bool? raw, bool? apache, PropertyNaming? propertyNames, bool? requiredMembers, bool? recordTypes, EquatableArray<string> warnings)
    {
        Namespace = ns;
        Raw = raw;
        Apache = apache;
        PropertyNames = propertyNames;
        RequiredMembers = requiredMembers;
        RecordTypes = recordTypes;
        Warnings = warnings;
    }

    public static FileSettings None { get; } = new(null, null, null, null, null, null, new EquatableArray<string>([]));

    public string? Namespace { get; }

    public bool? Raw { get; }

    public bool? Apache { get; }

    public PropertyNaming? PropertyNames { get; }

    public bool? RequiredMembers { get; }

    public bool? RecordTypes { get; }

    public EquatableArray<string> Warnings { get; }

    /// <summary>Whether the file sets anything of its own.</summary>
    public bool IsEmpty => Namespace is null && Raw is null && Apache is null && PropertyNames is null && RequiredMembers is null && RecordTypes is null;

    public static FileSettings Read(AnalyzerConfigOptions options, string path)
    {
        var warnings = new List<string>();
        string? Value(string name) => ProjectSettings.GetKey(options, Prefix + name);
        string? Choice(string name, string[] choices) =>
            ProjectSettings.Choice(Value(name), $"{name} of {path}", choices, fallback: null, warnings);

        var ns = Value("AvroSharpNamespace");
        if (ns is not null && !Array.TrueForAll(ns.Split('.'), part => AvroSharp.Schemas.AvroNames.IsValidName(part.AsSpan())))
        {
            warnings.Add($"AvroSharpNamespace of {path} is '{ns}', which is not a C# namespace; the project's is used.");
            ns = null;
        }

        var raw = Choice("AvroSharpLogicalTypes", ["native", "raw"]) is { } logical ? string.Equals(logical, "raw", StringComparison.Ordinal) : (bool?)null;
        var apache = Choice("AvroSharpApacheCompatible", ["true", "false"]) is { } a ? string.Equals(a, "true", StringComparison.Ordinal) : (bool?)null;
        PropertyNaming? naming = Choice("AvroSharpPropertyNames", ["pascal", "avro"]) switch
        {
            "pascal" => PropertyNaming.PascalCase,
            "avro" => PropertyNaming.Avro,
            _ => null,
        };
        var required = Choice("AvroSharpRequiredMembers", ["true", "false"]) is { } r ? string.Equals(r, "true", StringComparison.Ordinal) : (bool?)null;
        var record = Choice("AvroSharpTypeKind", ["class", "record"]) is { } k ? string.Equals(k, "record", StringComparison.Ordinal) : (bool?)null;
        return new FileSettings(ns, raw, apache, naming, required, record, new EquatableArray<string>(warnings));
    }

    /// <summary>The project's options, with this file's settings in their place.</summary>
    public CodeGenOptions Apply(CodeGenOptions project, bool hasApache) => IsEmpty ? project : new CodeGenOptions
    {
        Namespace = Namespace ?? project.Namespace,
        NamespaceMap = project.NamespaceMap,
        LogicalTypes = Raw is { } raw ? (raw ? LogicalTypeMapping.Raw : LogicalTypeMapping.Native) : project.LogicalTypes,
        TargetHasDateOnly = project.TargetHasDateOnly,
        ApacheCompatible = Apache is { } apache ? apache && hasApache : project.ApacheCompatible,
        NullableAnnotations = project.NullableAnnotations,
        LanguageVersion = project.LanguageVersion,
        PropertyNames = PropertyNames ?? project.PropertyNames,
        RequiredMembers = RequiredMembers ?? project.RequiredMembers,
        TypeKind = RecordTypes is { } recordTypes ? (recordTypes ? CodeGenTypeKind.Record : CodeGenTypeKind.Class) : project.TypeKind,
    };

    public bool Equals(FileSettings? other) =>
        other is not null
        && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
        && Raw == other.Raw
        && Apache == other.Apache
        && PropertyNames == other.PropertyNames
        && RequiredMembers == other.RequiredMembers
        && RecordTypes == other.RecordTypes
        && Warnings.Equals(other.Warnings);

    public override bool Equals(object? obj) => obj is FileSettings other && Equals(other);

    public override int GetHashCode() => Namespace is null ? 0 : StringComparer.Ordinal.GetHashCode(Namespace);
}
