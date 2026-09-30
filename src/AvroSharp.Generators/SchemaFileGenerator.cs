using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using AvroSharp.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AvroSharp.Generators;

/// <summary>
/// Generates C# types and serializers for the Avro schema files (<c>.avsc</c>) passed to the compiler as
/// <c>AdditionalFiles</c>. Files may refer to named types defined in other files.
/// </summary>
/// <remarks>
/// MSBuild properties configure it: see <see cref="ProjectSettings"/>. <c>AvroSharpNamespace</c>, for example, is the C#
/// namespace for Avro types without a namespace.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SchemaFileGenerator : IIncrementalGenerator
{
    private const string Category = "AvroSharp";

    private static readonly DiagnosticDescriptor s_invalidSchema = new(
        "AVROGEN001", "Invalid Avro schema", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_missingRuntime = new(
        "AVROGEN002",
        "AvroSharp runtime not referenced",
        "Code generated from Avro schemas needs the AvroSharp package; add a reference to it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_generationFailed = new(
        "AVROGEN003", "Avro code generation failed", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_renamed = new(
        "AVROGEN005", "Generated name changed", "{0}", Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_missingApache = new(
        "AVROGEN004",
        "Apache.Avro not referenced",
        "AvroSharpApacheCompatible is true, but the project does not reference Apache.Avro; add a reference to it, or remove the property",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_invalidSetting = new(
        "AVROGEN006", "Unrecognized AvroSharp setting", "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".avsc", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => new SchemaFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var properties = context.AnalyzerConfigOptionsProvider.Select(static (options, _) => ProjectSettings.Read(options.GlobalOptions));

        var hasRuntime = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName("AvroSharp.Serialization.Generated.AvroGeneratedCode") is not null);

        // A multi-targeted project runs the generator once per framework: DateOnly/TimeOnly exist from .NET 6.
        var target = context.CompilationProvider.Select(static (compilation, _) =>
        (
            HasDateOnly: compilation.GetTypeByMetadataName("System.DateOnly") is not null,
            HasApache: compilation.GetTypeByMetadataName("Avro.Specific.ISpecificRecord") is not null));

        // The project's major C# version: nullable annotations need 8, static abstract members 11.
        // netstandard2.0 and .NET Framework projects default to C# 7.3.
        var language = context.ParseOptionsProvider.Select(static (options, _) =>
            options is CSharpParseOptions csharp ? (int)csharp.LanguageVersion.MapSpecifiedToEffectiveVersion() / 100 : 7);

        var results = files.Combine(properties).Combine(target).Combine(language)
            .Select(static (input, cancellationToken) => Generate(
                input.Left.Left.Left,
                new CodeGenOptions
                {
                    Namespace = input.Left.Left.Right.Namespace,
                    NamespaceMap = input.Left.Left.Right.NamespaceMap,
                    LogicalTypes = input.Left.Left.Right.Raw ? LogicalTypeMapping.Raw : LogicalTypeMapping.Native,
                    TargetHasDateOnly = input.Left.Right.HasDateOnly,
                    ApacheCompatible = input.Left.Left.Right.Apache && input.Left.Right.HasApache,
                    NullableAnnotations = input.Right >= 8,
                    LanguageVersion = input.Right,
                    PropertyNames = input.Left.Left.Right.PropertyNames,
                },
                cancellationToken))
            .WithTrackingName("Generate");

        var apacheMissing = properties.Combine(target).Select(static (input, _) => input.Left.Apache && !input.Right.HasApache);

        context.RegisterSourceOutput(results.Combine(hasRuntime).Combine(apacheMissing), static (output, input) => AddOutput(output, input.Left.Left, input.Left.Right, input.Right));
        context.RegisterSourceOutput(properties, static (output, settings) =>
        {
            foreach (var warning in settings.Warnings)
            {
                output.ReportDiagnostic(Diagnostic.Create(s_invalidSetting, Location.None, warning));
            }
        });
    }

    /// <summary>Reports the diagnostics and, when the project can compile it, adds the generated code.</summary>
    private static void AddOutput(SourceProductionContext output, GenerationResult result, bool runtime, bool missingApache)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            output.ReportDiagnostic(diagnostic.ToDiagnostic());
        }

        if (result.Sources.Count == 0)
        {
            return;
        }

        if (!runtime)
        {
            output.ReportDiagnostic(Diagnostic.Create(s_missingRuntime, Location.None));
            return;
        }

        if (missingApache)
        {
            output.ReportDiagnostic(Diagnostic.Create(s_missingApache, Location.None));
            return;
        }

        // Hint names must be unique ignoring case, so types whose names differ only by case (cs.Order and cs.order)
        // made the generator fail and drop all its output (#131). The later one gets a number: hint names only name
        // the generated files.
        var hintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in result.Sources)
        {
            var hintName = source.HintName;
            for (var n = 2; !hintNames.Add(hintName); n++)
            {
                hintName = source.HintName[..^".g.cs".Length] + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".g.cs";
            }

            output.AddSource(hintName, SourceText.From(source.Text, System.Text.Encoding.UTF8));
        }
    }

    /// <summary>Parses every file together (see <see cref="SchemaFileSet"/>), then generates code for all of them.</summary>
    private static GenerationResult Generate(ImmutableArray<SchemaFile> files, CodeGenOptions options, CancellationToken cancellationToken)
    {
        var set = SchemaFileSet.Parse(files.Select(file => (file.Path, file.Text)), cancellationToken);
        var parsed = set.Schemas;
        var definedIn = set.DefinedIn;
        var diagnostics = set.Errors.Keys.OrderBy(path => path, StringComparer.Ordinal).Select(path => DiagnosticInfo.InvalidSchema(path, set)).ToList();
        IReadOnlyList<GeneratedSource> sources = [];
        try
        {
            sources = CSharpCodeGenerator.Generate(parsed, options);

            // Notes about the generated code (renamed properties) are reported at the file that defines the type.
            foreach (var source in sources)
            {
                var typeName = source.HintName[..^".g.cs".Length];
                var path = definedIn.TryGetValue(typeName, out var file) ? file : null;
                diagnostics.AddRange(source.Notes.Select(note => new DiagnosticInfo(s_renamed.Id, note, path, 1, 1)));
            }
        }
        catch (Exception ex) when (ex is AvroException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new DiagnosticInfo(s_generationFailed.Id, ex.Message, null, 0, 0));
        }

        return new GenerationResult(new EquatableArray<GeneratedSource>(sources), new EquatableArray<DiagnosticInfo>(diagnostics));
    }

    private sealed class SchemaFile(string path, string text) : IEquatable<SchemaFile>
    {
        public string Path { get; } = path;

        public string Text { get; } = text;

        public bool Equals(SchemaFile? other) =>
            other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal) && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SchemaFile other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path);
    }

    private sealed class GenerationResult(EquatableArray<GeneratedSource> sources, EquatableArray<DiagnosticInfo> diagnostics) : IEquatable<GenerationResult>
    {
        public EquatableArray<GeneratedSource> Sources { get; } = sources;

        public EquatableArray<DiagnosticInfo> Diagnostics { get; } = diagnostics;

        public bool Equals(GenerationResult? other) => other is not null && Sources.Equals(other.Sources) && Diagnostics.Equals(other.Diagnostics);

        public override bool Equals(object? obj) => obj is GenerationResult other && Equals(other);

        public override int GetHashCode() => Sources.GetHashCode();
    }

    /// <summary>A diagnostic without Roslyn objects, so it can be cached and compared.</summary>
    private sealed class DiagnosticInfo(string id, string message, string? path, long line, long column) : IEquatable<DiagnosticInfo>
    {
        public string Id { get; } = id;

        public string Message { get; } = message;

        public string? Path { get; } = path;

        public long Line { get; } = line;

        public long Column { get; } = column;

        public static DiagnosticInfo InvalidSchema(string path, SchemaFileSet set)
        {
            var ex = set.Errors[path];
            return new(s_invalidSchema.Id, set.GetErrorMessage(path), path, ex.LineNumber ?? 1, ex.BytePositionInLine ?? 1);
        }

        public Diagnostic ToDiagnostic()
        {
            var descriptor = string.Equals(Id, s_invalidSchema.Id, StringComparison.Ordinal) ? s_invalidSchema
                : string.Equals(Id, s_renamed.Id, StringComparison.Ordinal) ? s_renamed
                : s_generationFailed;
            var location = Path is null
                ? Location.None
                : Location.Create(Path, default, new LinePositionSpan(Position(), Position()));
            return Diagnostic.Create(descriptor, location, Message);
        }

        public bool Equals(DiagnosticInfo? other) =>
            other is not null
            && string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && Line == other.Line
            && Column == other.Column;

        public override bool Equals(object? obj) => obj is DiagnosticInfo other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Message);

        private LinePosition Position() => new((int)Math.Max(0, Line - 1), (int)Math.Max(0, Column - 1));
    }
}
