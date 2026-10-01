using System;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions.Enums;

namespace AvroSharp.Generators.Tests;

/// <summary>Generated code that did not compile, or lost data, in the 2026-09-29 review (#131).</summary>
public class GeneratedCodeReviewTests
{
    // What a .NET 10 project defines, so the generated code's .NET-only branches are compiled too.
    private static readonly string[] s_net10Symbols =
        ["NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"];

    /// <summary>
    /// Arrays whose items have no codec were written with a loop over the list's span, which C# 12 cannot compile on
    /// .NET 9 and 10 (CS9202), so a project pinned to C# 12 got code that did not build.
    /// </summary>
    [Test]
    [Arguments(LanguageVersion.CSharp12)]
    [Arguments(LanguageVersion.CSharp13)]
    [Arguments(LanguageVersion.Latest)]
    public async Task ArraysOfEveryItemShape_CompileOnNet10_WithTheProjectsLanguageVersion(LanguageVersion version)
    {
        const string Schema = """
            {"type":"record","name":"Arrays","namespace":"arr","fields":[
              {"name":"kinds","type":{"type":"array","items":{"type":"enum","name":"Kind","symbols":["A","B"]}}},
              {"name":"hashes","type":{"type":"array","items":{"type":"fixed","name":"Hash","size":2}}},
              {"name":"unions","type":{"type":"array","items":["null","int","string"]}},
              {"name":"maybes","type":{"type":"array","items":["null","int"]}},
              {"name":"days","type":{"type":"array","items":{"type":"int","logicalType":"date"}}},
              {"name":"nested","type":{"type":"array","items":{"type":"array","items":"long"}}},
              {"name":"byKind","type":{"type":"map","values":{"type":"array","items":"Kind"}}}
            ]}
            """;

        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
            [("arrays.avsc", Schema)], languageVersion: version, preprocessorSymbols: s_net10Symbols);

        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    private const string DefaultsSchema = """
        {"type":"record","name":"All","namespace":"defaults","fields":[
          {"name":"fixedDefault","type":{"type":"fixed","name":"F2","size":2},"default":"\u0001\u0002"},
          {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"a","type":"int"},{"name":"s","type":"string"}]},"default":{"a":7,"s":"x"}},
          {"name":"inners","type":{"type":"array","items":"Inner"},"default":[{"a":1,"s":"p"},{"a":2,"s":"q"}]},
          {"name":"day","type":{"type":"int","logicalType":"date"},"default":1},
          {"name":"at","type":{"type":"long","logicalType":"timestamp-millis"},"default":1000},
          {"name":"id","type":{"type":"string","logicalType":"uuid"},"default":"123e4567-e89b-12d3-a456-426614174000"},
          {"name":"money","type":{"type":"bytes","logicalType":"decimal","precision":10,"scale":2},"default":"\u0001"},
          {"name":"maybeDay","type":[{"type":"int","logicalType":"date"},"null"],"default":2},
          {"name":"maybeInner","type":["Inner","null"],"default":{"a":3,"s":"y"}},
          {"name":"maybeNot","type":["null","Inner"],"default":null},
          {"name":"plain","type":"int","default":5}
        ]}
        """;

    /// <summary>
    /// Defaults with no C# literal (records, fixed values, logical types, and collections and unions of them) were
    /// dropped: a fixed default was left null, so the new value could not even be written. Every field of
    /// <c>new All()</c> must now hold what a reader gives it when the data lacks the field.
    /// </summary>
    [Test]
    public async Task NewT_GivesEveryFieldItsSchemaDefault_AsAReaderWould()
    {
        var type = GeneratorHarness.GenerateAndLoad([("defaults.avsc", DefaultsSchema)]).GetType("defaults.All")!;
        var value = Activator.CreateInstance(type)!;
        var bytes = (byte[])type.GetMethod("ToAvroBytes", Type.EmptyTypes)!.Invoke(value, null)!;

        // A reader resolving data without any of the fields (a writer schema with none) fills in every default.
        var schema = AvroSchema.Parse(DefaultsSchema);
        var empty = AvroSchema.Parse("""{"type":"record","name":"All","namespace":"defaults","fields":[]}""");
        var expected = GenericDatumWriter.Create(schema).WriteToArray(GenericDatumReader.Create(empty, schema).Read([]));
        await Assert.That(bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);

        await Assert.That(type.GetProperty("Money")!.GetValue(value)).IsEqualTo(0.01m);
        await Assert.That(type.GetProperty("Day")!.GetValue(value)).IsEqualTo(new DateOnly(1970, 1, 2));
        await Assert.That(type.GetProperty("Id")!.GetValue(value)).IsEqualTo(Guid.Parse("123e4567-e89b-12d3-a456-426614174000"));
        await Assert.That(type.GetProperty("MaybeNot")!.GetValue(value)).IsNull();
        await Assert.That(type.GetProperty("Plain")!.GetValue(value)).IsEqualTo(5);
    }

    /// <summary>Each constructed value gets its own copies: mutating one's default record or list leaves the next alone.</summary>
    [Test]
    public async Task DefaultRecordsAndLists_AreNotShared_BetweenInstances()
    {
        var type = GeneratorHarness.GenerateAndLoad([("defaults.avsc", DefaultsSchema)]).GetType("defaults.All")!;
        var first = Activator.CreateInstance(type)!;
        var second = Activator.CreateInstance(type)!;

        var inners = (System.Collections.IList)type.GetProperty("Inners")!.GetValue(first)!;
        inners.Clear();

        await Assert.That(((System.Collections.IList)type.GetProperty("Inners")!.GetValue(second)!).Count).IsEqualTo(2);
        await Assert.That(type.GetProperty("Inner")!.GetValue(first)).IsNotSameReferenceAs(type.GetProperty("Inner")!.GetValue(second));
    }

    /// <summary>A float or double default beyond the type's range was written as <c>Infinityf</c>, which is not C#.</summary>
    [Test]
    public async Task FloatingDefaultsBeyondTheRange_Compile_AsInfinity()
    {
        const string Schema = """
            {"type":"record","name":"Big","namespace":"big","fields":[
              {"name":"f","type":"float","default":1e39},
              {"name":"g","type":"float","default":-1e39},
              {"name":"d","type":"double","default":1e400}
            ]}
            """;

        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("big.avsc", Schema)]);
        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();

        var type = GeneratorHarness.GenerateAndLoad([("big.avsc", Schema)]).GetType("big.Big")!;
        var value = Activator.CreateInstance(type)!;
        await Assert.That(type.GetProperty("F")!.GetValue(value)).IsEqualTo(float.PositiveInfinity);
        await Assert.That(type.GetProperty("G")!.GetValue(value)).IsEqualTo(float.NegativeInfinity);
        await Assert.That(type.GetProperty("D")!.GetValue(value)).IsEqualTo(double.PositiveInfinity);
    }

    /// <summary>
    /// The generated-code plan decoded a stored default with the limit on zero-size items that guards input, so a
    /// default of more such items than the limit failed every read of older data (#162). The default comes from the
    /// type's own schema: it is read, here 70,000 nulls, alone and inside a record default.
    /// </summary>
    [Test]
    public async Task ADefaultOverTheZeroSizeLimit_IsRead_ByAGeneratedType()
    {
        var nulls = string.Join(",", Enumerable.Repeat("null", 70_000));
        var schema = $$$"""
            {"type":"record","name":"Big","namespace":"zero","fields":[
              {"name":"a","type":"int"},
              {"name":"z","type":{"type":"array","items":"null"},"default":[{{{nulls}}}]},
              {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"n","type":{"type":"array","items":"null"}}]},"default":{"n":[{{{nulls}}}]}}
            ]}
            """;
        const string Probe = """
            public static class Probe
            {
                public static object Read(byte[] data, AvroSharp.Schemas.AvroSchema writer) => zero.Big.FromAvroBytes(data, writer);
            }
            """;
        var writer = AvroSchema.Parse("""{"type":"record","name":"Big","namespace":"zero","fields":[{"name":"a","type":"int"}]}""");
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(new GenericRecord((RecordSchema)writer) { ["a"] = 7 });
        var assembly = GeneratorHarness.GenerateAndLoad([("zero.avsc", schema)], extraSource: Probe);

        var value = assembly.GetType("Probe")!.GetMethod("Read")!.Invoke(null, [bytes, writer])!;
        var type = value.GetType();
        var inner = type.GetProperty("Inner")!.GetValue(value)!;

        await Assert.That(type.GetProperty("A")!.GetValue(value)).IsEqualTo(7);
        await Assert.That(((System.Collections.ICollection)type.GetProperty("Z")!.GetValue(value)!).Count).IsEqualTo(70_000);
        await Assert.That(((System.Collections.ICollection)inner.GetType().GetProperty("N")!.GetValue(inner)!).Count).IsEqualTo(70_000);
    }

    /// <summary>
    /// A union's default is for the first branch it is a value of (Avro 1.12), which the generator took to be always the
    /// first branch: a string default of <c>["int","string"]</c> failed to generate, and one of <c>[enum,"string"]</c> that
    /// isn't a symbol generated a symbol that doesn't exist. The defaults now match what a reader gives the field.
    /// </summary>
    [Test]
    public async Task UnionDefaults_AreForTheBranchTheyAreAValueOf()
    {
        const string Schema = """
            {"type":"record","name":"U","namespace":"unions","fields":[
              {"name":"n","type":["int","string"],"default":"abc"},
              {"name":"c","type":[{"type":"enum","name":"Color","symbols":["RED"]},"string"],"default":"BLUE"},
              {"name":"r","type":[{"type":"enum","name":"Tone","symbols":["RED"]},"string"],"default":"RED"}
            ]}
            """;

        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("unions.avsc", Schema)]);
        await Assert.That(generatorDiagnostics).IsEmpty();
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();

        var type = GeneratorHarness.GenerateAndLoad([("unions.avsc", Schema)]).GetType("unions.U")!;
        var value = Activator.CreateInstance(type)!;
        await Assert.That(type.GetProperty("N")!.GetValue(value)).IsEqualTo("abc");
        await Assert.That(type.GetProperty("C")!.GetValue(value)).IsEqualTo("BLUE");
        await Assert.That(type.GetProperty("R")!.GetValue(value)!.ToString()).IsEqualTo("RED");
    }

    /// <summary>
    /// A decimal default beyond System.Decimal or the precision is valid in the schema, but the generated constructor
    /// threw (#141). It is a generation error, in a union's later branch too.
    /// </summary>
    [Test]
    [Arguments("""{"type":"fixed","name":"Wide","size":16,"logicalType":"decimal","precision":28}""", "\"\\u007f\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\\u00ff\"", "does not fit in System.Decimal")]
    [Arguments("""{"type":"array","items":{"type":"bytes","logicalType":"decimal","precision":2}}""", "[\"\\u0001\\u0000\"]", "digits")]
    [Arguments("""["boolean",{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}]""", "\"\\u0027\\u0010\"", "digits")]
    public async Task DecimalDefaultsTheCSharpDecimalCannotHold_AreAnError(string type, string defaultValue, string reason)
    {
        var schema = $$"""{"type":"record","name":"Big","namespace":"dec","fields":[{"name":"d","type":{{type}},"default":{{defaultValue}}}]}""";

        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run([("dec.avsc", schema)]);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("The default of field 'd' has a decimal that the generated C# decimal cannot hold");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains(reason);
        await Assert.That(sources).IsEmpty();
    }

    /// <summary>
    /// A type named like a member the generator adds to it (or an enum named like one of its symbols) did not compile
    /// (CS0542). It is renamed, with a note.
    /// </summary>
    [Test]
    public async Task TypesNamedLikeTheirGeneratedMembers_AreRenamed_WithANote()
    {
        const string Schema = """
            {"type":"record","name":"Holder","namespace":"clash","fields":[
              {"name":"a","type":{"type":"record","name":"Schema","fields":[{"name":"x","type":"int"}]}},
              {"name":"b","type":{"type":"record","name":"ToAvroBytes","fields":[{"name":"x","type":"int"}]}},
              {"name":"c","type":{"type":"fixed","name":"Size","size":2}},
              {"name":"d","type":{"type":"fixed","name":"Value","size":2}},
              {"name":"f","type":{"type":"fixed","name":"Equals","size":2}},
              {"name":"e","type":{"type":"enum","name":"Color","symbols":["Red","Color"]}}
            ]}
            """;

        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("clash.avsc", Schema)]);

        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        var notes = generatorDiagnostics.Where(d => string.Equals(d.Id, "AVROGEN005", StringComparison.Ordinal)).Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await Assert.That(notes).Contains("Type 'clash.Schema' is C# type Schema_: C# does not allow a member named like its type, and Schema is a member the generator adds to it.");
        await Assert.That(notes).Contains("Type 'clash.Color' is C# type Color_: C# does not allow a member named like its type, and Color is a symbol of the enum.");
        await Assert.That(notes.Count).IsEqualTo(6);

        // The renamed types still read and write their schema.
        var type = GeneratorHarness.GenerateAndLoad([("clash.avsc", Schema)]).GetType("clash.Schema_")!;
        var value = Activator.CreateInstance(type)!;
        type.GetProperty("X")!.SetValue(value, 3);
        await Assert.That((byte[])type.GetMethod("ToAvroBytes", Type.EmptyTypes)!.Invoke(value, null)!).IsEquivalentTo(new byte[] { 0x06 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ATypeNamedLikeItsMembers_IsAnError_InTheApacheCompatibilityMode()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
            [("s.avsc", """{"type":"record","name":"Schema","namespace":"clash","fields":[{"name":"x","type":"int"}]}""")],
            apacheCompatible: true, referenceApache: true);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Rename the type in the schema.");
        await Assert.That(sources).IsEmpty();
    }

    /// <summary>Contextual keywords as type names or namespace segments produced code that did not compile.</summary>
    [Test]
    public async Task ContextualKeywords_AsTypeAndNamespaceNames_Compile()
    {
        const string Schema = """
            {"type":"record","name":"Holder","namespace":"nameof.value","fields":[
              {"name":"a","type":{"type":"record","name":"var","fields":[{"name":"x","type":"int"}]}},
              {"name":"b","type":{"type":"record","name":"record","fields":[{"name":"x","type":"int"}]}},
              {"name":"c","type":{"type":"record","name":"file","fields":[{"name":"x","type":"int"}]}},
              {"name":"d","type":{"type":"enum","name":"scoped","symbols":["A"]}},
              {"name":"e","type":{"type":"enum","name":"required","symbols":["A"]}},
              {"name":"f","type":{"type":"fixed","name":"partial","size":1}},
              {"name":"g","type":{"type":"record","name":"not","fields":[{"name":"x","type":"int"}]}},
              {"name":"h","type":{"type":"record","name":"_","fields":[{"name":"x","type":"int"}]}}
            ]}
            """;

        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run([("kw.avsc", Schema)]);

        // A type named var is renamed: C# would read the generated code's implicitly typed locals as that type.
        var note = generatorDiagnostics.Single();
        await Assert.That(note.Id).IsEqualTo("AVROGEN005");
        await Assert.That(note.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo("Type 'nameof.value.var' is C# type var_: C# would read the generated code's implicitly typed locals as that type.");
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    [Arguments("My Models")]
    [Arguments("My-Models")]
    [Arguments("1abc")]
    public async Task AnInvalidDefaultNamespace_IsReported(string ns)
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
            [("p.avsc", """{"type":"record","name":"Plain","fields":[{"name":"x","type":"int"}]}""")], avroSharpNamespace: ns);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains($"The namespace '{ns}' is not a C# namespace");
        await Assert.That(sources).IsEmpty();
    }

    /// <summary>A record whose full name is another type's namespace did not compile (CS0101).</summary>
    [Test]
    public async Task ARecordNamedLikeAnotherTypesNamespace_IsReported()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run(
        [
            ("events.avsc", """{"type":"record","name":"events","namespace":"app","fields":[{"name":"x","type":"int"}]}"""),
            ("click.avsc", """{"type":"record","name":"Click","namespace":"app.events","fields":[{"name":"x","type":"int"}]}"""),
        ]);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("The type 'app.events' is the C# type app.events, which is also the namespace of 'app.events.Click'.");
        await Assert.That(sources).IsEmpty();
    }

    /// <summary>Types whose names differ only by case compile as two types in the source generator.</summary>
    [Test]
    public async Task TypesWhoseNamesDifferOnlyByCase_AreBothGenerated()
    {
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
        [
            ("upper.avsc", """{"type":"record","name":"Order","namespace":"cs","fields":[{"name":"x","type":"int"}]}"""),
            ("lower.avsc", """{"type":"record","name":"order","namespace":"cs","fields":[{"name":"x","type":"int"}]}"""),
        ]);

        await Assert.That(generatorDiagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Length).IsEqualTo(2);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    private const string SentinelSchema = """
        {"type":"record","name":"Lease","namespace":"raw","fields":[
          {"name":"expires","type":{"type":"long","logicalType":"timestamp-millis","avrosharp.raw":true}},
          {"name":"starts","type":{"type":"long","logicalType":"timestamp-millis"}}
        ]}
        """;

    /// <summary>
    /// Java reads a timestamp of Long.MaxValue, a common "never" sentinel, but DateTimeOffset can't hold it, so a
    /// generated type failed every file that had one. "avrosharp.raw": true keeps one schema's underlying type, so the
    /// field is a long and reads it, while the others keep their .NET types.
    /// </summary>
    [Test]
    public async Task ARawLogicalType_IsItsUnderlyingType_AndReadsValuesDotNetCannotHold()
    {
        const string Reader = """
            public static class SentinelReader
            {
                public static object Read(byte[] data) => raw.Lease.FromAvroBytes(data);
            }
            """;
        var assembly = GeneratorHarness.GenerateAndLoad([("lease.avsc", SentinelSchema)], extraSource: Reader);
        var type = assembly.GetType("raw.Lease")!;
        var schema = (RecordSchema)AvroSchema.Parse(SentinelSchema);
        var data = GenericDatumWriter.Create(schema).WriteToArray(new GenericRecord(schema) { ["expires"] = long.MaxValue, ["starts"] = 0L });

        var lease = assembly.GetType("SentinelReader")!.GetMethod("Read")!.Invoke(null, [data])!;

        await Assert.That(type.GetProperty("Expires")!.PropertyType).IsEqualTo(typeof(long));
        await Assert.That(type.GetProperty("Starts")!.PropertyType).IsEqualTo(typeof(DateTimeOffset));
        await Assert.That(type.GetProperty("Expires")!.GetValue(lease)).IsEqualTo(long.MaxValue);
        await Assert.That(type.GetProperty("Starts")!.GetValue(lease)).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    /// <summary>Without "avrosharp.raw", such a value fails with a message that says how to read it.</summary>
    [Test]
    public async Task AnOutOfRangeTimestamp_FailsWithTheWayToReadIt()
    {
        var ex = Assert.Throws<AvroDataException>(() => AvroSharp.Serialization.AvroLogicalValues.TimestampFromMilliseconds(long.MaxValue));

        await Assert.That(ex.Message).Contains("outside the range of DateTime").And.Contains("\"avrosharp.raw\": true");
    }

    [Test]
    public async Task ARawLogicalType_IsReported_InApacheCompatibleMode()
    {
        var (sources, generatorDiagnostics, _) = GeneratorHarness.Run([("lease.avsc", SentinelSchema)], apacheCompatible: true, referenceApache: true);

        var diagnostic = generatorDiagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("\"avrosharp.raw\" is not available with AvroSharpApacheCompatible");
        await Assert.That(sources).IsEmpty();
    }
}
