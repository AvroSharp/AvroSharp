using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AvroSharp.Generators;

/// <summary>
/// Generates C# types and serializers for the Avro schema files (<c>.avsc</c>) passed to the compiler as
/// <c>AdditionalFiles</c>. Files may refer to named types defined in other files.
/// </summary>
/// <remarks>
/// Set the MSBuild property <c>AvroSharpNamespace</c> to choose the C# namespace for Avro types without a namespace.
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

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".avsc", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => new SchemaFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var properties = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
        (
            Namespace: options.GlobalOptions.TryGetValue("build_property.AvroSharpNamespace", out var ns) && !string.IsNullOrWhiteSpace(ns) ? ns.Trim() : null,
            Raw: options.GlobalOptions.TryGetValue("build_property.AvroSharpLogicalTypes", out var logical)
                && string.Equals(logical.Trim(), "raw", StringComparison.OrdinalIgnoreCase)));

        var hasRuntime = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName("AvroSharp.Serialization.AvroGeneratedCode") is not null);

        // A multi-targeted project runs the generator once per framework: DateOnly/TimeOnly exist from .NET 6.
        var hasDateOnly = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName("System.DateOnly") is not null);

        var results = files.Combine(properties).Combine(hasDateOnly)
            .Select(static (input, cancellationToken) => Generate(
                input.Left.Left,
                new CodeGenOptions
                {
                    DefaultNamespace = input.Left.Right.Namespace,
                    LogicalTypes = input.Left.Right.Raw ? LogicalTypeMapping.Raw : LogicalTypeMapping.Native,
                    TargetHasDateOnly = input.Right,
                },
                cancellationToken))
            .WithTrackingName("Generate");

        context.RegisterSourceOutput(results.Combine(hasRuntime), static (output, input) =>
        {
            var (result, runtime) = input;
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

            foreach (var source in result.Sources)
            {
                output.AddSource(source.HintName, SourceText.From(source.Text, System.Text.Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// Parses every file with one parser, retrying files whose references are not defined yet, then generates code
    /// for all of them together.
    /// </summary>
    private static GenerationResult Generate(ImmutableArray<SchemaFile> files, CodeGenOptions options, CancellationToken cancellationToken)
    {
        var parser = new AvroSchemaParser();
        var parsed = new List<AvroSchema>();
        var pending = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var errors = new Dictionary<SchemaFile, AvroSchemaException>();
        for (var progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var file in pending.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    parsed.Add(parser.Parse(file.Text));
                    pending.Remove(file);
                    errors.Remove(file);
                    progress = true;
                }
                catch (AvroSchemaException ex)
                {
                    errors[file] = ex;
                }
            }
        }

        var diagnostics = pending.Select(file => DiagnosticInfo.InvalidSchema(file.Path, errors[file])).ToList();
        IReadOnlyList<GeneratedSource> sources = [];
        try
        {
            sources = CSharpCodeGenerator.Generate(parsed, options);
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

        public static DiagnosticInfo InvalidSchema(string path, AvroSchemaException ex) =>
            new(s_invalidSchema.Id, ex.Message, path, ex.LineNumber ?? 1, ex.BytePositionInLine ?? 1);

        public Diagnostic ToDiagnostic()
        {
            var descriptor = string.Equals(Id, s_invalidSchema.Id, StringComparison.Ordinal) ? s_invalidSchema : s_generationFailed;
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
