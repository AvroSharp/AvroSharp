using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions.Enums;

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
            .IsEquivalentTo(new[] { "app.events.Event.g.cs", "app.events.Hash.g.cs", "app.events.Kind.g.cs" }, CollectionOrdering.Any);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    [Arguments("app.events.Event.g.cs")]
    [Arguments("app.events.Hash.g.cs")]
    [Arguments("app.events.Kind.g.cs")]
    public async Task Output_MatchesTheCommittedSnapshot(string hintName)
    {
        var (sources, _, _) = GeneratorHarness.Run([("event.avsc", EventSchema)]);
        // The generator's version, in [GeneratedCode], changes with every build; snapshots hold a placeholder.
        var actual = WithoutVersion(sources.Single(s => string.Equals(s.HintName, hintName, StringComparison.Ordinal)).SourceText.ToString());

        // Snapshots are read from the copy in the output folder: CI builds map source paths to /_/ (deterministic
        // builds), so the source folder is only known, and only written, when updating snapshots locally.
        var snapshot = Path.Combine(AppContext.BaseDirectory, "Snapshots", hintName);
        if (string.Equals(Environment.GetEnvironmentVariable("AVROSHARP_UPDATE_SNAPSHOTS"), "1", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(SnapshotDirectory(), hintName), actual);
            return;
        }

        var expected = WithoutVersion(await File.ReadAllTextAsync(snapshot));
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
    [Arguments(LanguageVersion.CSharp7_2, false)]
    [Arguments(LanguageVersion.CSharp7_2, true)]
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

    /// <summary>
    /// The generated code needs C# 7.2 (readonly structs). C# 7.0 was read as version 0 and 7.1 as 7, which generated
    /// code that didn't compile; both are now an error that says what to set.
    /// </summary>
    [Test]
    [Arguments(LanguageVersion.CSharp7)]
    [Arguments(LanguageVersion.CSharp7_1)]
    public async Task CSharp7_0And7_1_AreAnError(LanguageVersion version)
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
            [("order.avsc", """{"type":"record","name":"Order","fields":[{"name":"id","type":"long"}]}""")], languageVersion: version);

        await Assert.That(sources).IsEmpty();
        await Assert.That(generatorDiagnostics.Select(d => (d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))).IsEquivalentTo(
            [("AVROGEN003", "The generated code needs C# 7.2 or later, and the project uses C# 7.0 or 7.1: set <LangVersion> to 7.3 or later.")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
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

    /// <summary>
    /// A union whose branches map to the same C# type cannot be written (the branch is chosen from the value's runtime
    /// type) and would not compile (duplicate case labels, CS8120), so it is reported instead (#108).
    /// </summary>
    [Test]
    [Arguments("""{"type":"string","logicalType":"uuid"}""", """{"type":"fixed","name":"U16","size":16,"logicalType":"uuid"}""", "global::System.Guid")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}""", """{"type":"fixed","name":"D8","size":8,"logicalType":"decimal","precision":10,"scale":2}""", "decimal")]
    [Arguments("""{"type":"int","logicalType":"time-millis"}""", """{"type":"long","logicalType":"time-micros"}""", "global::System.TimeOnly")]
    public async Task UnionBranchesWithTheSameCSharpType_AreReported(string first, string second, string csharpType)
    {
        var schema = $$"""{"type":"record","name":"R","namespace":"u","fields":[{"name":"v","type":["null",{{first}},{{second}}]}]}""";

        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run([("u.avsc", schema)]);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains($"both map to the C# type {csharpType},");
        await Assert.That(sources).IsEmpty();

        // Mapping logical types to their underlying types separates the branches, and the output compiles.
        var (rawSources, rawDiagnostics, rawCompile) = GeneratorHarness.Run([("u.avsc", schema)], logicalTypes: "raw");
        await Assert.That(rawDiagnostics).IsEmpty();
        await Assert.That(rawSources).IsNotEmpty();
        await Assert.That(rawCompile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>
    /// C# ends a comment at any of its line terminators, so a doc containing one must not let the rest of the doc
    /// become code (#110): the output compiles without warnings and has no member the doc tried to add.
    /// </summary>
    [Test]
    [Arguments("\u2028")]
    [Arguments("\u2029")]
    [Arguments("\u0085")]
    [Arguments("\r")]
    [Arguments("\n")]
    [Arguments("\r\n")]
    public async Task DocLineTerminators_CannotInjectCode(string terminator)
    {
        var injected = CSharpJson(terminator + "public int Injected { get; set; }" + terminator + "\u0001end");
        var schema = $$$"""
            {"type":"record","name":"Doc","namespace":"d","doc":"{{{injected}}}","fields":[
              {"name":"v","type":"int","doc":"{{{injected}}}"},
              {"name":"e","type":{"type":"enum","name":"E","doc":"{{{injected}}}","symbols":["A"]}}
            ]}
            """;

        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("d.avsc", schema)]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(sources).IsNotEmpty();
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        var type = GeneratorHarness.GenerateAndLoad([("d.avsc", schema)]).GetType("d.Doc")!;
        await Assert.That(type.GetProperty("Injected")).IsNull();

        // JSON with every non-ASCII or control character escaped, so the terminators reach the parser intact.
        static string CSharpJson(string text) => string.Concat(text.Select(c => c is < (char)0x20 or > (char)0x7E
            ? "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
            : c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
        await Assert.That(sources.Select(s => s.HintName).ToArray()).IsEquivalentTo(new[] { "x.Color.g.cs" }, CollectionOrdering.Any);
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
            .IsEquivalentTo(new[] { "crm.Customer.g.cs", "geo.Address.g.cs", "retail.Shop.g.cs" }, CollectionOrdering.Any);
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
            // The JSON is split into concatenated literals; join them back.
            var text = sources.Single(s => string.Equals(s.HintName, record, StringComparison.Ordinal)).SourceText.ToString();
            await Assert.That(System.Text.RegularExpressions.Regex.Replace(text, "\"(?:u8)?\\s*\\+\\s*\"", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5))).Contains("{\\\"type\\\":\\\"record\\\",\\\"name\\\":\\\"Address\\\"");
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
        await Assert.That(sources.Select(s => s.HintName).ToArray()).IsEquivalentTo(new[] { "Fine.g.cs" }, CollectionOrdering.Any);
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
    [Arguments("raw", "public int Day { get; set; }", "Id = \"\";")]
    [Arguments("RAW", "public int Day { get; set; }", "Id = \"\";")]
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

    /// <summary>
    /// Code written against avrogen's generated classes compiles unchanged against the compatibility mode's output
    /// (#116): the static <c>_SCHEMA</c>, the instance <c>Schema</c>, avrogen's (Avro) property names, and Apache's
    /// specific writer and reader created from them.
    /// </summary>
    [Test]
    public async Task ApacheCompatible_AcceptsCodeWrittenForAvrogenClasses()
    {
        const string AvrogenStyle = """
            using Avro.IO;
            using Avro.Specific;

            internal static class AvrogenStyle
            {
                public static byte[] RoundTrip(app.events.Event value)
                {
                    value.id = 1;
                    value.tags.Add("t");
                    var writer = new SpecificDatumWriter<app.events.Event>(value.Schema);
                    var stream = new System.IO.MemoryStream();
                    writer.Write(value, new BinaryEncoder(stream));
                    stream.Position = 0;
                    var reader = new SpecificDatumReader<app.events.Event>(app.events.Event._SCHEMA, app.events.Event._SCHEMA);
                    var copy = reader.Read(null!, new BinaryDecoder(stream));
                    global::AvroSharp.Schemas.AvroSchema own = app.events.Event.AvroSharpSchema;
                    Avro.Schema fixedSchema = app.events.Hash._SCHEMA;
                    return copy.ToAvroBytes();
                }
            }
            """;
        var compilation = GeneratorHarness.CreateCompilation(referenceAvroSharp: true, referenceApache: true, LanguageVersion.Latest, AvrogenStyle);
        GeneratorHarness.CreateDriver([("event.avsc", EventSchema)], apacheCompatible: true)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>
    /// <c>SchemaJson</c> is a constant, on every C# version: usable in attributes, <c>const</c> fields and <c>switch</c>
    /// cases, as in 0.1.
    /// </summary>
    [Test]
    [Arguments(LanguageVersion.CSharp7_3)]
    [Arguments(LanguageVersion.Latest)]
    public async Task SchemaJson_IsAConstant(LanguageVersion version)
    {
        const string Usage = """
            [System.ComponentModel.Description(app.events.Event.SchemaJson)]
            internal static class UsesTheSchema
            {
                public const string Copy = app.events.Hash.SchemaJson;

                public static bool IsEventSchema(string json)
                {
                    switch (json)
                    {
                        case app.events.Event.SchemaJson:
                            return true;
                        default:
                            return false;
                    }
                }
            }
            """;
        var compilation = GeneratorHarness.CreateCompilation(referenceAvroSharp: true, referenceApache: false, version, Usage);
        GeneratorHarness.CreateDriver([("event.avsc", EventSchema)], languageVersion: version)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>
    /// Names (#117): capitals are title-cased, a property renamed to avoid a clash is reported (AVROGEN005), and an
    /// all-lower-case type name compiles without CS8981.
    /// </summary>
    [Test]
    public async Task Naming_TitleCasesCapitals_ReportsRenames_AndAllowsLowerCaseTypeNames()
    {
        const string Schema = """
            {"type":"record","name":"block","namespace":"event","fields":[
              {"name":"USER_ID","type":"long"},
              {"name":"userId","type":"string"},
              {"name":"HTTP2_PORT","type":"int"},
              {"name":"txId","type":"long"}
            ]}
            """;

        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("block.avsc", Schema)]);

        var text = sources.Single().SourceText.ToString();
        await Assert.That(text).Contains("public long UserId { get; set; }");
        await Assert.That(text).Contains("public string UserId_ { get; set; }");
        await Assert.That(text).Contains("public int Http2Port { get; set; }");
        await Assert.That(text).Contains("public long TxId { get; set; }");
        var renamed = generatorDiagnostics.Single();
        await Assert.That(renamed.Id).IsEqualTo("AVROGEN005");
        await Assert.That(renamed.Severity).IsEqualTo(DiagnosticSeverity.Info);
        await Assert.That(renamed.Location.GetLineSpan().Path).IsEqualTo("block.avsc");
        await Assert.That(renamed.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo("Field 'event.block.userId' is property UserId_: UserId is taken by field 'USER_ID'.");
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>
    /// Debugging and documentation (#117): records show their first fields in the debugger, the serializers are not
    /// stepped into, and a logical type that keeps its raw type says what the value means.
    /// </summary>
    [Test]
    public async Task Output_HasDebuggerAttributes_AndDocumentsRawLogicalTypes()
    {
        const string Schema = """
            {"type":"record","name":"Reading","namespace":"d","fields":[
              {"name":"id","type":"long"},
              {"name":"tags","type":{"type":"array","items":"string"}},
              {"name":"at","type":{"type":"long","logicalType":"timestamp-nanos"}},
              {"name":"amount","type":{"type":"bytes","logicalType":"decimal","precision":30,"scale":2}}
            ]}
            """;

        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("d.avsc", Schema)]);

        await Assert.That(generatorDiagnostics).IsEmpty();
        var text = sources.Single().SourceText.ToString();
        await Assert.That(text).Contains("[global::System.Diagnostics.DebuggerDisplay(\"Id = {Id}, At = {At}\")]");
        await Assert.That(text).Contains("[global::System.Diagnostics.DebuggerNonUserCode]\n        internal static void WriteCore(");
        await Assert.That(text).Contains("/// <remarks>Avro timestamp-nanos: nanoseconds since 1970-01-01T00:00:00Z.</remarks>");
        await Assert.That(text).Contains("/// <remarks>Avro decimal(30,2): the unscaled value as big-endian two's-complement bytes; the value is unscaled / 10^2.</remarks>");
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
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

        await Assert.That(record.Get(2)).IsEqualTo("stored");
        await Assert.That(record.Get(3)).IsEqualTo(42);
        var fieldValue = record.GetType().GetProperty(setting is null ? "FieldValue" : "fieldValue")!.GetValue(record);
        await Assert.That(fieldValue).IsEqualTo("stored");
    }

    [Test]
    public async Task EditingCSharpCode_ReusesTheCachedGeneration()
    {
        var compilation = GeneratorHarness.CreateCompilation(true, "class A { }");
        var driver = GeneratorHarness.CreateDriver([("event.avsc", EventSchema)]).RunGenerators(compilation);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class B { }")));

        var step = driver.GetRunResult().Results.Single().TrackedSteps["Generate"].Single();
        await Assert.That(step.Outputs.Select(o => o.Reason).ToArray()).IsEquivalentTo(new[] { IncrementalStepRunReason.Cached }, CollectionOrdering.Any);
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
        await Assert.That(step.Outputs.Select(o => o.Reason).ToArray()).IsEquivalentTo(new[] { IncrementalStepRunReason.Cached }, CollectionOrdering.Any);
    }

    [Test]
    public async Task AnEditThatChangesNoOutput_LeavesTheGenerationUnchanged()
    {
        // Trailing whitespace regenerates, but the sources and diagnostics compare equal, so later steps are skipped.
        const string Clash = """{"type":"record","name":"Clash","namespace":"app.events","fields":[{"name":"USER_ID","type":"long"},{"name":"userId","type":"long"}]}""";
        var compilation = GeneratorHarness.CreateCompilation(true, "class A { }");
        var driver = GeneratorHarness.CreateDriver([("event.avsc", EventSchema), ("clash.avsc", Clash)]).RunGenerators(compilation);
        await Assert.That(driver.GetRunResult().Diagnostics.Select(d => d.Id).ToArray()).IsEquivalentTo(new[] { "AVROGEN005" }, CollectionOrdering.Any);

        driver = driver.ReplaceAdditionalTexts([GeneratorHarness.Text("event.avsc", EventSchema + "\n  "), GeneratorHarness.Text("clash.avsc", Clash + " ")]).RunGenerators(compilation);

        var step = driver.GetRunResult().Results.Single().TrackedSteps["Generate"].Single();
        await Assert.That(step.Outputs.Select(o => o.Reason).ToArray()).IsEquivalentTo(new[] { IncrementalStepRunReason.Unchanged }, CollectionOrdering.Any);
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

    private static string WithoutVersion(string source) => System.Text.RegularExpressions.Regex.Replace(
        source, "GeneratedCode\\(\"AvroSharp\\.CodeGen\", \"[^\"]*\"\\)", "GeneratedCode(\"AvroSharp.CodeGen\", \"<version>\")", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));

    private static string SnapshotDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");
}
