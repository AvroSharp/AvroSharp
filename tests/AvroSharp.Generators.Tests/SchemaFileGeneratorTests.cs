using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

public class SchemaFileGeneratorTests
{
    private const string EventSchema = """
        {"type":"record","name":"Event","namespace":"app.events","doc":"Something that happened.","fields":[
          {"name":"id","type":"long"},
          {"name":"kind","type":{"type":"enum","name":"Kind","symbols":["CREATED","DELETED"]}},
          {"name":"hash","type":{"type":"fixed","name":"Hash","size":2}},
          {"name":"tags","type":{"type":"array","items":"string"}},
          {"name":"scores","type":{"type":"map","values":"double"}},
          {"name":"parent","type":["null","Event"]},
          {"name":"payload","type":["null","int","string"]}
        ]}
        """;

    [Test]
    public async Task EachNamedType_GetsOneFile_AndTheOutputCompilesWithoutWarnings()
    {
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("event.avsc", EventSchema)]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources.Select(s => s.HintName).ToArray())
            .IsEquivalentTo(new[] { "app.events.Event.g.cs", "app.events.Hash.g.cs", "app.events.Kind.g.cs" });
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    [Arguments("app.events.Event.g.cs")]
    [Arguments("app.events.Hash.g.cs")]
    [Arguments("app.events.Kind.g.cs")]
    public async Task Output_MatchesTheCommittedSnapshot(string hintName)
    {
        var (sources, _, _) = GeneratorHarness.Run([("event.avsc", EventSchema)]);
        var actual = sources.Single(s => string.Equals(s.HintName, hintName, StringComparison.Ordinal)).SourceText.ToString();

        // Snapshots are read from the copy in the output folder: CI builds map source paths to /_/ (deterministic
        // builds), so the source folder is only known, and only written, when updating snapshots locally.
        var snapshot = Path.Combine(AppContext.BaseDirectory, "Snapshots", hintName);
        if (string.Equals(Environment.GetEnvironmentVariable("AVROSHARP_UPDATE_SNAPSHOTS"), "1", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(SnapshotDirectory(), hintName), actual);
            return;
        }

        var expected = await File.ReadAllTextAsync(snapshot);
        if (!string.Equals(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(snapshot + ".received", actual);
            throw new InvalidOperationException(
                $"Generated code differs from {snapshot}; the new output is in {hintName}.received. If the change is intended, run the tests with AVROSHARP_UPDATE_SNAPSHOTS=1.");
        }
    }

    [Test]
    public async Task NamedTypesFromOtherFiles_ResolveInAnyFileOrder()
    {
        // a.avsc sorts first but uses a type that b.avsc defines.
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
        [
            ("a.avsc", """{"type":"record","name":"Holder","namespace":"x","fields":[{"name":"color","type":"y.Color"}]}"""),
            ("b.avsc", """{"type":"enum","name":"Color","namespace":"y","symbols":["RED"]}"""),
        ]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources.Length).IsEqualTo(2);
        await Assert.That(compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task InvalidSchema_IsReportedAtItsLineAndColumn()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
        [
            ("bad.avsc", "{\"type\":\"record\",\"name\":\"R\",\n \"fields\":[{\"name\":\"a\",\"type\":\"nope\"}]}"),
            ("good.avsc", """{"type":"enum","name":"Fine","symbols":["A"]}"""),
        ]);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN001");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("nope");
        await Assert.That(diagnostic.Location.GetLineSpan().Path).IsEqualTo("bad.avsc");
        await Assert.That(diagnostic.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(1);

        // The valid file is still generated.
        await Assert.That(sources.Select(s => s.HintName).ToArray()).IsEquivalentTo(new[] { "Fine.g.cs" });
    }

    [Test]
    public async Task MissingRuntimeReference_IsReported()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run([("event.avsc", EventSchema)], referenceAvroSharp: false);

        await Assert.That(generatorDiagnostics.Single().Id).IsEqualTo("AVROGEN002");
        await Assert.That(sources).IsEmpty();
    }

    [Test]
    public async Task AvroSharpNamespace_IsUsedForTypesWithoutANamespace()
    {
        var (sources, _, compileDiagnostics) = GeneratorHarness.Run(
            [("plain.avsc", """{"type":"record","name":"Plain","fields":[{"name":"x","type":"int"}]}""")],
            avroSharpNamespace: "My.Models");

        await Assert.That(sources.Single().SourceText.ToString()).Contains("namespace My.Models");
        await Assert.That(compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    [Arguments(null, "public global::System.DateOnly Day { get; set; }", "public global::System.Guid Id { get; set; }")]
    [Arguments("raw", "public int Day { get; set; }", "public string Id { get; set; } = \"\";")]
    [Arguments("RAW", "public int Day { get; set; }", "public string Id { get; set; } = \"\";")]
    public async Task AvroSharpLogicalTypes_SelectsNativeOrRawMapping(string? setting, string day, string id)
    {
        var (sources, _, compileDiagnostics) = GeneratorHarness.Run(
            [("t.avsc", """{"type":"record","name":"T","fields":[{"name":"day","type":{"type":"int","logicalType":"date"}},{"name":"id","type":{"type":"string","logicalType":"uuid"}}]}""")],
            logicalTypes: setting);

        var text = sources.Single().SourceText.ToString();
        await Assert.That(text).Contains(day);
        await Assert.That(text).Contains(id);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task ApacheCompatibleWithoutApacheAvro_IsReported()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run([("event.avsc", EventSchema)], apacheCompatible: true);

        await Assert.That(generatorDiagnostics.Single().Id).IsEqualTo("AVROGEN004");
        await Assert.That(sources).IsEmpty();
    }

    [Test]
    public async Task ApacheCompatible_ImplementsApacheContracts_AndCompiles()
    {
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("event.avsc", EventSchema)], apacheCompatible: true, referenceApache: true);

        await Assert.That(generatorDiagnostics).IsEmpty();
        var byName = sources.ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);
        await Assert.That(byName["app.events.Event.g.cs"]).Contains("global::Avro.Specific.ISpecificRecord");
        await Assert.That(byName["app.events.Hash.g.cs"]).Contains(": global::Avro.Specific.SpecificFixed");
        await Assert.That(byName.ContainsKey("AvroSharp.Generated.ApacheDecimals.g.cs")).IsTrue();
        await Assert.That(compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task EditingCSharpCode_ReusesTheCachedGeneration()
    {
        var compilation = GeneratorHarness.CreateCompilation(true, "class A { }");
        var driver = GeneratorHarness.CreateDriver([("event.avsc", EventSchema)]).RunGenerators(compilation);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class B { }")));

        var step = driver.GetRunResult().Results.Single().TrackedSteps["Generate"].Single();
        await Assert.That(step.Outputs.Select(o => o.Reason).ToArray()).IsEquivalentTo(new[] { IncrementalStepRunReason.Cached });
    }

    [Test]
    public async Task ReloadedSchemaFilesWithTheSameContent_ReuseTheCachedGeneration()
    {
        // The IDE hands the generator new AdditionalText objects when files are reloaded; equal content must not
        // trigger a new generation.
        var compilation = GeneratorHarness.CreateCompilation(true, "class A { }");
        var driver = GeneratorHarness.CreateDriver([("event.avsc", EventSchema)]).RunGenerators(compilation);

        driver = driver.ReplaceAdditionalTexts([GeneratorHarness.Text("event.avsc", EventSchema)]).RunGenerators(compilation);

        var step = driver.GetRunResult().Results.Single().TrackedSteps["Generate"].Single();
        await Assert.That(step.Outputs.Select(o => o.Reason).ToArray()).IsEquivalentTo(new[] { IncrementalStepRunReason.Cached });
    }

    [Test]
    public async Task EditingASchema_Regenerates()
    {
        var compilation = GeneratorHarness.CreateCompilation(true, "class A { }");
        var driver = GeneratorHarness.CreateDriver([("event.avsc", EventSchema)]).RunGenerators(compilation);
        var original = driver.GetRunResult().Results.Single().GeneratedSources;

        var edited = GeneratorHarness.CreateDriver([("event.avsc", EventSchema.Replace("\"DELETED\"", "\"DELETED\",\"MOVED\"", StringComparison.Ordinal))]).RunGenerators(compilation);
        var kind = edited.GetRunResult().Results.Single().GeneratedSources.Single(s => string.Equals(s.HintName, "app.events.Kind.g.cs", StringComparison.Ordinal));

        await Assert.That(kind.SourceText.ToString()).Contains("MOVED = 2,");
        await Assert.That(original.Single(s => string.Equals(s.HintName, "app.events.Kind.g.cs", StringComparison.Ordinal)).SourceText.ToString()).DoesNotContain("MOVED");
    }

    private static string SnapshotDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");
}
