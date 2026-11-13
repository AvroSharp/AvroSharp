using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>Options per schema file, as AdditionalFiles metadata that overrides the project's MSBuild properties (#18).</summary>
public class PerFileOptionsTests
{
    // Two types without an Avro namespace; Holder uses Moment, from the other file.
    private const string Moment = """{"type":"record","name":"Moment","fields":[{"name":"day_of_year","type":{"type":"int","logicalType":"date"}}]}""";
    private const string Holder = """{"type":"record","name":"Holder","fields":[{"name":"the_moment","type":"Moment"},{"name":"when","type":{"type":"int","logicalType":"date"}}]}""";

    private static readonly string[] s_net10 =
        ["NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"];

    [Test]
    public async Task AFilesSettings_ApplyToTheTypesItDefines_AndOthersFindThemByTheirNames()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("moment.avsc", Moment), ("holder.avsc", Holder)],
            Properties(("AvroSharpNamespace", "Acme.Main")),
            Metadata(("moment.avsc", [("AvroSharpNamespace", "Acme.Legacy"), ("AvroSharpPropertyNames", "avro"), ("AvroSharpLogicalTypes", "raw")])),
            s_net10);

        var moment = Source(sources, "Moment");
        var holder = Source(sources, "Holder");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(moment).Contains("namespace Acme.Legacy");
        await Assert.That(moment).Contains("public int day_of_year { get; set; }");
        await Assert.That(holder).Contains("namespace Acme.Main");
        await Assert.That(holder).Contains("public global::Acme.Legacy.Moment TheMoment { get; set; }");
        await Assert.That(holder).Contains("public global::System.DateOnly When { get; set; }");
    }

    [Test]
    public async Task RequiredMembersAndRecords_CanBeOneFilesOwn()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("moment.avsc", Moment), ("holder.avsc", Holder)],
            Properties(("AvroSharpRequiredMembers", "true")),
            Metadata(("moment.avsc", [("AvroSharpRequiredMembers", "false"), ("AvroSharpTypeKind", "record")])),
            s_net10);

        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(Source(sources, "Moment")).Contains("public sealed partial record class Moment");
        await Assert.That(Source(sources, "Moment")).DoesNotContain("required");
        await Assert.That(Source(sources, "Holder")).Contains("public required global::Moment TheMoment");
    }

    [Test]
    public async Task AFileInTheApacheMode_WithoutApacheAvro_IsReported()
    {
        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("moment.avsc", Moment), ("holder.avsc", Holder)],
            Properties(),
            Metadata(("moment.avsc", [("AvroSharpApacheCompatible", "true")])));

        await Assert.That(diagnostics.Select(d => d.Id).ToList()).IsEquivalentTo(["AVROGEN004"], TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(sources).IsEmpty();
    }

    [Test]
    public async Task ApacheModeTypes_AndNativeOnes_ComposeInOneProject()
    {
        // The code generator itself, with Apache.Avro referenced: Moment in the compatibility mode, Holder native.
        var schemas = new[] { AvroSchema.Parse(Moment) };
        var parser = new AvroSchemaParser();
        parser.AddNamedSchemas(schemas[0]);
        var holder = parser.Parse(Holder);
        var apache = new CodeGenOptions { ApacheCompatible = true };
        var sources = CSharpCodeGenerator.Generate([schemas[0], holder], CodeGenOptions.Default, schema => string.Equals(schema.Name.Name, "Moment", StringComparison.Ordinal) ? apache : null);
        const string Use = """
            public static class Use
            {
                public static string RoundTrip()
                {
                    var holder = new Holder { TheMoment = new Moment { day_of_year = new System.DateTime(2026, 11, 13) }, When = new System.DateOnly(2026, 11, 14) };
                    var read = Holder.FromAvroBytes(holder.ToAvroBytes());
                    return read.TheMoment.day_of_year.ToString("yyyy-MM-dd") + " " + read.When.ToString("yyyy-MM-dd");
                }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(true, true, LanguageVersion.Latest, s_net10, [.. sources.Select(s => s.Text), Use]);
        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image);
        await Assert.That(emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        var result = (string)System.Reflection.Assembly.Load(image.ToArray()).GetType("Use")!.GetMethod("RoundTrip")!.Invoke(null, null)!;

        await Assert.That(sources.Any(s => string.Equals(s.Namespace, "AvroSharp.Generated", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result).IsEqualTo("2026-11-13 2026-11-14");
    }

    [Test]
    public async Task UnrecognizedFileSettings_AreWarnings_NamingTheFile()
    {
        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("moment.avsc", Moment)],
            Properties(),
            Metadata(("moment.avsc", [("AvroSharpTypeKind", "struct"), ("AvroSharpNamespace", "Not A Namespace")])));

        var messages = diagnostics.Where(d => string.Equals(d.Id, "AVROGEN006", StringComparison.Ordinal)).Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages.All(m => m.Contains("moment.avsc", StringComparison.Ordinal))).IsTrue();
        await Assert.That(sources.Length).IsEqualTo(1);
    }

    [Test]
    public async Task TypeOptions_MayNotChangeProjectWideSettings()
    {
        var schema = AvroSchema.Parse(Moment);

        var ex = Assert.Throws<ArgumentException>(() => CSharpCodeGenerator.Generate([schema], CodeGenOptions.Default, _ => new CodeGenOptions { LanguageVersion = 9 }));

        await Assert.That(ex.Message).Contains("LanguageVersion");
    }

    private static Dictionary<string, string> Properties(params (string Name, string Value)[] properties) =>
        properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Metadata(params (string Path, (string Name, string Value)[] Values)[] files) =>
        files.ToDictionary(f => f.Path, f => (IReadOnlyDictionary<string, string>)f.Values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Source(System.Collections.Immutable.ImmutableArray<GeneratedSourceResult> sources, string type) =>
        sources.Single(s => s.HintName.EndsWith(type + ".g.cs", StringComparison.Ordinal)).SourceText.ToString();
}
