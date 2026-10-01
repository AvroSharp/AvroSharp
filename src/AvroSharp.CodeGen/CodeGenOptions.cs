using System.Collections.Generic;

namespace AvroSharp.CodeGen;

/// <summary>Options for <see cref="CSharpCodeGenerator"/>.</summary>
/// <seealso cref="CSharpCodeGenerator"/>
/// <seealso cref="GeneratedSource"/>
public sealed class CodeGenOptions
{
    /// <summary>Gets the default options.</summary>
    public static CodeGenOptions Default { get; } = new();

    /// <summary>
    /// Gets the C# namespace for named types that have no Avro namespace. Defaults to <see langword="null"/>: such
    /// types are generated in the global namespace.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Gets C# namespaces for Avro namespaces, as Apache.Avro's avrogen maps them with <c>--namespace avro:csharp</c>.
    /// A type's namespace is replaced by the longest key that equals it or is a prefix of it at a <c>.</c>: with
    /// <c>com.example</c> mapped to <c>Example</c>, <c>com.example.events</c> becomes <c>Example.events</c>. Only the C#
    /// namespace changes; the schema, and so the data and fingerprints, do not. Not supported with
    /// <see cref="ApacheCompatible"/>, since Apache.Avro finds generated types by the schema's full name. Defaults to
    /// <see langword="null"/>: no mapping.
    /// </summary>
    public IReadOnlyDictionary<string, string>? NamespaceMap { get; init; }

    /// <summary>Gets how values of logical types are represented. Defaults to <see cref="LogicalTypeMapping.Native"/>.</summary>
    public LogicalTypeMapping LogicalTypes { get; init; } = LogicalTypeMapping.Native;

    /// <summary>
    /// Gets whether the target framework has <c>System.DateOnly</c> and <c>System.TimeOnly</c> (.NET 6 and later).
    /// When it does not, <c>date</c> maps to <c>DateTime</c> and <c>time-*</c> to <c>TimeSpan</c>. Defaults to <see langword="true"/>.
    /// </summary>
    public bool TargetHasDateOnly { get; init; } = true;

    /// <summary>
    /// Gets whether generated types also work with Apache.Avro's specific API (<c>SpecificDatumWriter</c>/<c>Reader</c>):
    /// records implement <c>Avro.Specific.ISpecificRecord</c>, fixed types derive from <c>Avro.Specific.SpecificFixed</c>,
    /// and logical types use Apache's .NET types (<c>DateTime</c> for dates and timestamps, <c>TimeSpan</c> for times,
    /// <c>Guid</c> for string UUIDs, <c>Avro.AvroDecimal</c> for decimals), whatever <see cref="LogicalTypes"/> says.
    /// The generated code then needs a reference to Apache.Avro. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ApacheCompatible { get; init; }

    /// <summary>
    /// Gets whether to emit nullable reference type annotations (<c>string?</c>, <c>#nullable enable</c>), which need
    /// C# 8 or later. Without them the generated code compiles as C# 7.3, the default for netstandard2.0 and .NET
    /// Framework projects. Defaults to <see langword="true"/>; with a <see cref="LanguageVersion"/> below 8 it must be
    /// <see langword="false"/>, or generation throws <see cref="System.ArgumentException"/>.
    /// </summary>
    public bool NullableAnnotations { get; init; } = true;

    /// <summary>
    /// Gets how record fields become property names, or <see langword="null"/> (the default) for the mode's own:
    /// <see cref="PropertyNaming.PascalCase"/>, or with <see cref="ApacheCompatible"/> <see cref="PropertyNaming.Avro"/>,
    /// the names Apache.Avro's avrogen uses.
    /// </summary>
    public PropertyNaming? PropertyNames { get; init; }

    /// <summary>
    /// Gets the major C# version the generated code may use. With 11 or later it adds, for .NET 8 and later,
    /// <c>IAvroSerializable&lt;T&gt;</c>. It must be 7 or later. Defaults to 14.
    /// </summary>
    public int LanguageVersion { get; init; } = 14;
}
