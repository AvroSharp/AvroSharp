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

    /// <summary>
    /// netstandard2.0 and .NET Framework projects default to C# 7.3; the generated code must compile there (#41),
    /// including every construct the generator emits (unions, collections, logical types, fixed, Get/Put, and the
    /// Apache compatibility mode).
    /// </summary>
    [Test]
    [Arguments(LanguageVersion.CSharp7_3, false)]
    [Arguments(LanguageVersion.CSharp7_3, true)]
    [Arguments(LanguageVersion.CSharp8, false)]
    [Arguments(LanguageVersion.CSharp8, true)]
    public async Task Output_CompilesWithOlderLanguageVersions(LanguageVersion version, bool apache)
    {
        const string Everything = """
            {"type":"record","name":"All","namespace":"old","fields":[
              {"name":"s","type":"string"},{"name":"b","type":"bytes"},
              {"name":"note","type":["null","string"]},{"name":"count","type":["null","int"]},
              {"name":"any","type":["null","int","string"]},
              {"name":"list","type":{"type":"array","items":"long"}},{"name":"map","type":{"type":"map","values":"string"}},
              {"name":"day","type":{"type":"int","logicalType":"date"}},
              {"name":"money","type":{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}},
              {"name":"hash","type":{"type":"fixed","name":"Hash","size":2}},
              {"name":"kind","type":{"type":"enum","name":"Kind","symbols":["A","B"]}},
              {"name":"next","type":["null","All"]}
            ]}
            """;

        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
            [("all.avsc", Everything)], apacheCompatible: apache, referenceApache: apache, languageVersion: version);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources.Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
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
    public async Task ATypeDefinedInTwoFiles_IsReported_NamingTheOtherFile()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
        [
            ("a.avsc", """{"type":"enum","name":"Color","namespace":"x","symbols":["RED"]}"""),
            ("b.avsc", """{"type":"enum","name":"Color","namespace":"x","symbols":["GREEN"]}"""),
        ]);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN001");
        await Assert.That(diagnostic.Location.GetLineSpan().Path).IsEqualTo("b.avsc");
        var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(message).Contains("'x.Color' is already defined");
        await Assert.That(message).EndsWith("It is also defined in a.avsc.");
        await Assert.That(sources.Select(s => s.HintName).ToArray()).IsEquivalentTo(new[] { "x.Color.g.cs" });
    }

    /// <summary>
    /// Schema sets written for Apache's one-file-at-a-time tooling inline shared types in every file (#45). Identical
    /// inlined definitions are accepted and generated once.
    /// </summary>
    [Test]
    public async Task ATypeInlinedIdenticallyInSeveralFiles_IsGeneratedOnce()
    {
        const string Address = """{"type":"record","name":"Address","namespace":"geo","fields":[{"name":"city","type":"string"}]}""";
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
        [
            ("customer.avsc", """{"type":"record","name":"Customer","namespace":"crm","fields":[{"name":"home","type":""" + Address + "}]}"),
            ("shop.avsc", """{"type":"record","name":"Shop","namespace":"retail","fields":[{"name":"site","type":""" + Address + "}]}"),
        ]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources.Select(s => s.HintName).OrderBy(h => h, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(new[] { "crm.Customer.g.cs", "geo.Address.g.cs", "retail.Shop.g.cs" });
        await Assert.That(compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task ATypeSharedByManyFiles_IsGeneratedOnce_AndEachRecordSchemaStandsAlone()
    {
        const string Address = """{"type":"record","name":"Address","namespace":"geo","fields":[{"name":"city","type":"string"}]}""";
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
        [
            ("address.avsc", Address),
            ("customer.avsc", """{"type":"record","name":"Customer","namespace":"crm","fields":[{"name":"home","type":"geo.Address"}]}"""),
            ("shop.avsc", """{"type":"record","name":"Shop","namespace":"retail","fields":[{"name":"site","type":"geo.Address"},{"name":"other","type":["null","geo.Address"]}]}"""),
            ("warehouse.avsc", """{"type":"record","name":"Warehouse","namespace":"ops","fields":[{"name":"docks","type":{"type":"array","items":"geo.Address"}}]}"""),
        ]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources.Count(s => string.Equals(s.HintName, "geo.Address.g.cs", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(sources.Length).IsEqualTo(4);
        await Assert.That(compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();

        // The embedded schema of every record that uses Address carries its definition, so it parses on its own.
        foreach (var record in new[] { "crm.Customer.g.cs", "retail.Shop.g.cs", "ops.Warehouse.g.cs" })
        {
            await Assert.That(sources.Single(s => string.Equals(s.HintName, record, StringComparison.Ordinal)).SourceText.ToString()).Contains("{\\\"type\\\":\\\"record\\\",\\\"name\\\":\\\"Address\\\"");
        }
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

    private const string NamingSchema = """
        {"type":"record","name":"Named","namespace":"naming","fields":[
          {"name":"customer_name","type":"string"},
          {"name":"class","type":"int"},
          {"name":"fieldValue","type":"string"},
          {"name":"fieldPos","type":"int"},
          {"name":"Schema","type":"string"}
        ]}
        """;

    [Test]
    [Arguments(null, "public string CustomerName { get; set; }", "public int Class { get; set; }")]
    [Arguments("avro", "public string customer_name { get; set; }", "public int @class { get; set; }")]
    [Arguments("AVRO", "public string customer_name { get; set; }", "public int @class { get; set; }")]
    public async Task AvroSharpPropertyNames_SelectsPascalCaseOrAvroNames(string? setting, string name, string keyword)
    {
        var (sources, _, compileDiagnostics) = GeneratorHarness.Run([("n.avsc", NamingSchema)], propertyNames: setting);

        var text = sources.Single().SourceText.ToString();
        await Assert.That(text).Contains(name);
        await Assert.That(text).Contains(keyword);
        await Assert.That(text).Contains("Schema_ { get; set; }"); // clashes with the generated Schema property either way
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>
    /// With Avro names, a field can be called like Put's parameters (fieldValue, fieldPos). The generated Get/Put
    /// qualify properties with <c>this.</c>, so the values land in the properties, not in the parameters.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("avro")]
    public async Task GetAndPut_ReachTheProperties_EvenWhenFieldsAreNamedLikeTheParameters(string? setting)
    {
        var assembly = GeneratorHarness.GenerateAndLoad([("n.avsc", NamingSchema)], setting);
        var record = (AvroSharp.Serialization.IAvroSpecificRecord)Activator.CreateInstance(assembly.GetType("naming.Named")!)!;

        record.Put(2, "stored");
        record.Put(3, 42);

        await Assert.That(record.Get(2)).IsEqualTo((object)"stored");
        await Assert.That(record.Get(3)).IsEqualTo((object)42);
        var fieldValue = record.GetType().GetProperty(setting is null ? "FieldValue" : "fieldValue")!.GetValue(record);
        await Assert.That(fieldValue).IsEqualTo((object)"stored");
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
