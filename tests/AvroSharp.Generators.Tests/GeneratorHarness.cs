using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AvroSharp.Generators.Tests;

/// <summary>Runs <see cref="SchemaFileGenerator"/> on in-memory schema files and compiles the result.</summary>
internal static class GeneratorHarness
{
    private static readonly Lazy<MetadataReference[]> s_frameworkReferences = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System", StringComparison.Ordinal) || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
            .Select(path => MetadataReference.CreateFromFile(path))]);

    public static CSharpCompilation CreateCompilation(bool referenceAvroSharp = true, params string[] sources) => CreateCompilation(referenceAvroSharp, referenceApache: false, LanguageVersion.Latest, sources);

    public static CSharpCompilation CreateCompilation(bool referenceAvroSharp, bool referenceApache, LanguageVersion languageVersion, params string[] sources) =>
        CreateCompilation(referenceAvroSharp, referenceApache, languageVersion, [], sources);

    // preprocessorSymbols: symbols such as NET8_0_OR_GREATER, so the code's .NET-only branches are compiled too.
    public static CSharpCompilation CreateCompilation(bool referenceAvroSharp, bool referenceApache, LanguageVersion languageVersion, string[] preprocessorSymbols, params string[] sources)
    {
        IEnumerable<MetadataReference> references = s_frameworkReferences.Value;
        if (referenceAvroSharp)
        {
            references = references.Append(MetadataReference.CreateFromFile(typeof(AvroSharp.IO.AvroWriter).Assembly.Location));
        }

        if (referenceApache)
        {
            references = references.Append(MetadataReference.CreateFromFile(typeof(Avro.Schema).Assembly.Location));
        }

        return CSharpCompilation.Create(
            "Consumer",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(languageVersion, preprocessorSymbols: preprocessorSymbols))),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: languageVersion >= LanguageVersion.CSharp8 ? NullableContextOptions.Enable : NullableContextOptions.Disable));
    }

    public static GeneratorDriver CreateDriver(IEnumerable<(string Path, string Text)> files, string? avroSharpNamespace = null, string? logicalTypes = null, bool apacheCompatible = false, LanguageVersion languageVersion = LanguageVersion.Latest, string? propertyNames = null, string[]? preprocessorSymbols = null) =>
        CSharpGeneratorDriver.Create(
            [new SchemaFileGenerator().AsSourceGenerator()],
            files.Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text)),
            new CSharpParseOptions(languageVersion, preprocessorSymbols: preprocessorSymbols ?? []),
            new Options(Properties(avroSharpNamespace, logicalTypes, apacheCompatible, propertyNames)),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    /// <summary>Runs the generator; returns the generated sources, the generator's diagnostics and the compiler's.</summary>
    public static (ImmutableArray<GeneratedSourceResult> Sources, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompileDiagnostics)
        Run(IEnumerable<(string Path, string Text)> files, string? avroSharpNamespace = null, bool referenceAvroSharp = true, string? logicalTypes = null, bool apacheCompatible = false, bool referenceApache = false, LanguageVersion languageVersion = LanguageVersion.Latest, string? propertyNames = null, string[]? preprocessorSymbols = null)
    {
        var compilation = CreateCompilation(referenceAvroSharp, referenceApache, languageVersion, preprocessorSymbols ?? [], "internal static class Placeholder { }");
        var driver = CreateDriver(files, avroSharpNamespace, logicalTypes, apacheCompatible, languageVersion, propertyNames, preprocessorSymbols).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var result = driver.GetRunResult().Results.Single();
        return (result.GeneratedSources, generatorDiagnostics, output.GetDiagnostics());
    }

    /// <summary>Runs the generator with these MSBuild properties (names without <c>build_property.</c>), as a project sets them.</summary>
    public static (ImmutableArray<GeneratedSourceResult> Sources, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompileDiagnostics)
        RunWithProperties(IEnumerable<(string Path, string Text)> files, IReadOnlyDictionary<string, string> msbuildProperties)
    {
        var compilation = CreateCompilation(true, "internal static class Placeholder { }");
        var properties = msbuildProperties.ToDictionary(p => "build_property." + p.Key, p => p.Value, StringComparer.Ordinal);
        var driver = CSharpGeneratorDriver.Create(
                [new SchemaFileGenerator().AsSourceGenerator()],
                files.Select(f => (AdditionalText)new InMemoryText(f.Path, f.Text)),
                new CSharpParseOptions(LanguageVersion.Latest),
                new Options(properties))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        return (driver.GetRunResult().Results.Single().GeneratedSources, generatorDiagnostics, output.GetDiagnostics());
    }

    /// <summary>
    /// Runs the generator, compiles its output and loads the assembly, so tests can call the generated code. Code in
    /// <paramref name="extraSource"/> is compiled with it, for calls that reflection can't make (span or ref parameters).
    /// </summary>
    public static System.Reflection.Assembly GenerateAndLoad(IEnumerable<(string Path, string Text)> files, string? propertyNames = null, string? extraSource = null)
    {
        var compilation = CreateCompilation(true, extraSource ?? "internal static class Placeholder { }");
        CreateDriver(files, propertyNames: propertyNames).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var image = new MemoryStream();
        var emitted = output.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        return System.Reflection.Assembly.Load(image.ToArray());
    }

    public static AdditionalText Text(string path, string text) => new InMemoryText(path, text);

    /// <summary>Runs <see cref="SerializableTypeGenerator"/> on C# sources; returns the generated sources, its diagnostics and the compiler's.</summary>
    public static (ImmutableArray<GeneratedSourceResult> Sources, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompileDiagnostics)
        RunTypes(LanguageVersion languageVersion, params string[] sources)
    {
        var compilation = CreateCompilation(true, false, languageVersion, ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"], sources);
        var driver = CSharpGeneratorDriver.Create(
                [new SerializableTypeGenerator().AsSourceGenerator()],
                parseOptions: new CSharpParseOptions(languageVersion, preprocessorSymbols: ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"]),
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        return (driver.GetRunResult().Results.Single().GeneratedSources, generatorDiagnostics, output.GetDiagnostics());
    }

    /// <inheritdoc cref="RunTypes(LanguageVersion, string[])"/>
    public static (ImmutableArray<GeneratedSourceResult> Sources, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompileDiagnostics)
        RunTypes(params string[] sources) => RunTypes(LanguageVersion.Latest, sources);

    /// <summary>Runs <see cref="SerializableTypeGenerator"/> on C# sources, compiles the result and loads it.</summary>
    public static System.Reflection.Assembly LoadTypes(params string[] sources)
    {
        var compilation = CreateCompilation(true, false, LanguageVersion.Latest, ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"], sources);
        CSharpGeneratorDriver.Create(
                [new SerializableTypeGenerator().AsSourceGenerator()],
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"]))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var image = new MemoryStream();
        var emitted = output.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return System.Reflection.Assembly.Load(image.ToArray());
    }

    /// <summary>The MSBuild properties the generator reads, as the compiler exposes them (build_property.*).</summary>
    private static Dictionary<string, string> Properties(string? avroSharpNamespace, string? logicalTypes, bool apacheCompatible, string? propertyNames)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (avroSharpNamespace is not null)
        {
            properties["build_property.AvroSharpNamespace"] = avroSharpNamespace;
        }

        if (logicalTypes is not null)
        {
            properties["build_property.AvroSharpLogicalTypes"] = logicalTypes;
        }

        if (apacheCompatible)
        {
            properties["build_property.AvroSharpApacheCompatible"] = "true";
        }

        if (propertyNames is not null)
        {
            properties["build_property.AvroSharpPropertyNames"] = propertyNames;
        }

        return properties;
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Options(Dictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(properties);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values.Empty;

        private sealed class Values(Dictionary<string, string> properties) : AnalyzerConfigOptions
        {
            public static Values Empty { get; } = new([]);

            public override bool TryGetValue(string key, out string value) =>
                properties.TryGetValue(key, out value!);
        }
    }
}
