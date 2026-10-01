using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>
/// AvroSchemaCompatibility (#165). The rows port Java's TestSchemaCompatibility tables (reader and writer swapped into
/// writer-first order: where Java says compatible the verdict is Compatible, where Java says incompatible it is Partial
/// or Incompatible), avro-rs's tricky cases and Apache.Avro C#'s alias rows; each row also checks that the verdict
/// agrees with GenericDatumReader.Create and with generated types' record plans.
/// </summary>
public class SchemaCompatibilityTests
{
    private const AvroCompatibilityVerdict Compatible = AvroCompatibilityVerdict.Compatible;
    private const AvroCompatibilityVerdict Partial = AvroCompatibilityVerdict.Partial;
    private const AvroCompatibilityVerdict Incompatible = AvroCompatibilityVerdict.Incompatible;

    private const string Enum1AB = """{"type":"enum","name":"Enum1","symbols":["A","B"]}""";
    private const string Enum1ABC = """{"type":"enum","name":"Enum1","symbols":["A","B","C"]}""";

    /// <summary>Writer schema, reader schema, verdict, issues and warnings (each "Kind@path", in order, joined with "; ").</summary>
    public static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> Pairs() =>
        Primitives().Concat(EnumsAndFixed()).Concat(Unions()).Concat(Records()).Concat(LogicalTypes());

    private static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> Primitives() =>
    [
        // Primitives and promotions.
        ("\"null\"", "\"null\"", Compatible, "", ""),
        ("\"int\"", "\"long\"", Compatible, "", ""),
        ("\"int\"", "\"float\"", Compatible, "", "LossyPromotion@$"),
        ("\"int\"", "\"double\"", Compatible, "", ""),
        ("\"long\"", "\"float\"", Compatible, "", "LossyPromotion@$"),
        ("\"long\"", "\"double\"", Compatible, "", "LossyPromotion@$"),
        ("\"float\"", "\"double\"", Compatible, "", ""),
        ("\"string\"", "\"bytes\"", Compatible, "", ""),
        ("\"bytes\"", "\"string\"", Compatible, "", "LossyPromotion@$"),
        ("\"int\"", "\"null\"", Incompatible, "TypeMismatch@$", ""),
        ("\"long\"", "\"int\"", Incompatible, "TypeMismatch@$", ""),
        ("\"double\"", "\"float\"", Incompatible, "TypeMismatch@$", ""),
        ("\"float\"", "\"long\"", Incompatible, "TypeMismatch@$", ""),
        ("\"int\"", "\"boolean\"", Incompatible, "TypeMismatch@$", ""),
        ("\"null\"", "\"int\"", Incompatible, "TypeMismatch@$", ""),
        ("\"string\"", "\"double\"", Incompatible, "TypeMismatch@$", ""),
        ("\"int\"", "\"string\"", Incompatible, "TypeMismatch@$", ""),
        ("\"int\"", """{"type":"record","name":"R","fields":[]}""", Incompatible, "TypeMismatch@$", ""),
        ("\"int\"", Enum1AB, Incompatible, "TypeMismatch@$", ""),

        // Arrays and maps.
        ("""{"type":"array","items":"int"}""", """{"type":"array","items":"long"}""", Compatible, "", ""),
        ("""{"type":"map","values":"int"}""", """{"type":"map","values":"long"}""", Compatible, "", ""),
        ("""{"type":"array","items":"long"}""", """{"type":"array","items":"int"}""", Incompatible, "TypeMismatch@$[]", ""),
        ("""{"type":"map","values":"long"}""", """{"type":"map","values":"int"}""", Incompatible, "TypeMismatch@${}", ""),
        ("""{"type":"array","items":"int"}""", """{"type":"map","values":"int"}""", Incompatible, "TypeMismatch@$", ""),
    ];

    private static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> EnumsAndFixed() =>
    [
        // Enums: missing symbols are partial (Java: MISSING_ENUM_SYMBOLS) unless the reader has a default.
        (Enum1AB, Enum1ABC, Compatible, "", ""),
        (Enum1ABC, Enum1AB, Partial, "MissingEnumSymbols@$", ""),
        (Enum1ABC, """{"type":"enum","name":"Enum1","symbols":["B","C"]}""", Partial, "MissingEnumSymbols@$", ""),
        (Enum1ABC, """{"type":"enum","name":"Enum1","symbols":["A","B"],"default":"A"}""", Compatible, "", "EnumDefaultUsed@$"),
        (Enum1AB, """{"type":"enum","name":"Enum1","symbols":["C","D"]}""", Incompatible, "MissingEnumSymbols@$", ""),
        (Enum1AB, """{"type":"enum","name":"Enum2","symbols":["A","B"]}""", Incompatible, "NameMismatch@$", ""),
        ("""{"type":"enum","name":"Enum1","namespace":"ns1","symbols":["A"]}""", """{"type":"enum","name":"Enum1","namespace":"ns2","symbols":["A"]}""", Compatible, "", "UnqualifiedNameMatch@$"),
        ("""{"type":"enum","name":"Old","namespace":"ns","symbols":["A"]}""", """{"type":"enum","name":"New","namespace":"ns","aliases":["Old"],"symbols":["A"]}""", Compatible, "", ""),

        // Fixed.
        ("""{"type":"fixed","name":"F","size":4}""", """{"type":"fixed","name":"F","size":4}""", Compatible, "", ""),
        ("""{"type":"fixed","name":"F","size":8}""", """{"type":"fixed","name":"F","size":4}""", Incompatible, "FixedSizeMismatch@$", ""),
        ("""{"type":"fixed","name":"F","size":4}""", """{"type":"fixed","name":"G","size":4}""", Incompatible, "NameMismatch@$", ""),
        ("\"string\"", """{"type":"fixed","name":"F","size":4}""", Incompatible, "TypeMismatch@$", ""),
    ];

    private static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> Unions() =>
    [
        // Unions. Each writer branch is resolved on its own; one with no counterpart is partial (Java: MISSING_UNION_BRANCH).
        ("[]", "[]", Compatible, "", ""),
        ("[]", "\"float\"", Compatible, "", ""),
        ("""["int","string"]""", """["string","int"]""", Compatible, "", ""),
        ("""["int","float"]""", "\"double\"", Compatible, "", ""),
        ("""["int","long"]""", "\"float\"", Compatible, "", "LossyPromotion@$[0:int]; LossyPromotion@$[1:long]"),
        ("\"int\"", """["null","int"]""", Compatible, "", ""),
        ("\"int\"", """["int"]""", Compatible, "", ""),
        ("""["int"]""", "\"int\"", Compatible, "", ""),
        ("\"int\"", """["null","long"]""", Compatible, "", ""),
        ("""["string","bytes"]""", """["bytes"]""", Compatible, "", ""),
        ("""["int","string"]""", """["int"]""", Partial, "MissingUnionBranch@$[1:string]", ""),
        ("""["int","long","float","double"]""", """["int"]""", Partial, "MissingUnionBranch@$[1:long]; MissingUnionBranch@$[2:float]; MissingUnionBranch@$[3:double]", ""),
        ("""["int","string","long"]""", """["int","string"]""", Partial, "MissingUnionBranch@$[2:long]", ""),
        ("""["null","int"]""", "\"int\"", Partial, "TypeMismatch@$[0:null]", ""),
        ("""["int","float"]""", "\"long\"", Partial, "TypeMismatch@$[1:float]", ""),
        ("""["null","string"]""", "\"int\"", Incompatible, "TypeMismatch@$[0:null]; TypeMismatch@$[1:string]", ""),
        ("\"string\"", """["int"]""", Incompatible, "MissingUnionBranch@$", ""),
        ("""{"type":"record","name":"R1","fields":[]}""", """["int",{"type":"record","name":"R2","fields":[]}]""", Incompatible, "MissingUnionBranch@$", ""),
    ];

    private static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> Records() =>
    [
        // Records.
        ("""{"type":"record","name":"R","fields":[]}""", """{"type":"record","name":"R","fields":[]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""", """{"type":"record","name":"R","fields":[]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int","default":0}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""", Incompatible, "MissingDefault@$.a", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"long"}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"long"}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""", Incompatible, "TypeMismatch@$.a", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int","default":0}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int","default":0},{"name":"b","type":"int","default":0}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R1","fields":[]}""", """{"type":"record","name":"R2","fields":[]}""", Incompatible, "NameMismatch@$", ""),
        ("""{"type":"record","name":"Record","namespace":"ns","fields":[]}""", """{"type":"record","name":"Record","fields":[]}""", Compatible, "", "UnqualifiedNameMatch@$"),

        // Aliases: the reader's record and field aliases name the writer's (Apache.Avro C#'s AliasTest rows, and the
        // alias-renamed field that Apache.Avro's CanRead accepts but whose data its readers lose).
        ("""{"type":"record","name":"Old","namespace":"ns","fields":[{"name":"x","type":"int"}]}""", """{"type":"record","name":"New","namespace":"ns","aliases":["Old"],"fields":[{"name":"x","type":"int"}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"old","type":"int"}]}""", """{"type":"record","name":"R","fields":[{"name":"new","type":"int","aliases":["old"]}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"old","type":"int"}]}""", """{"type":"record","name":"R","fields":[{"name":"new","type":"int","aliases":["wrong"]}]}""", Incompatible, "MissingDefault@$.new", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int","aliases":["b"]}]}""", Compatible, "", "AmbiguousFieldAlias@$.a"),

        // Nested locations (Java's /fields/1/type/... rows).
        ("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":["int","string"]}]}""", """{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":["int"]}]}""", Partial, "MissingUnionBranch@$.b[1:string]", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"e","type":{"type":"enum","name":"E","symbols":["A","B","C"]}}]}""", """{"type":"record","name":"R","fields":[{"name":"e","type":{"type":"enum","name":"E","symbols":["A","B"]}}]}""", Partial, "MissingEnumSymbols@$.e", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"f","type":{"type":"fixed","name":"F","size":8}}]}""", """{"type":"record","name":"R","fields":[{"name":"f","type":{"type":"fixed","name":"F","size":4}}]}""", Incompatible, "FixedSizeMismatch@$.f", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[{"name":"sku","type":"long"}]}}}]}""", """{"type":"record","name":"R","fields":[{"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[{"name":"sku","type":"int"}]}}}]}""", Incompatible, "TypeMismatch@$.items[].sku", ""),
        ("""{"type":"record","name":"R","fields":[{"name":"p","type":["null",{"type":"record","name":"Card","fields":[{"name":"number","type":"long"}]}]}]}""", """{"type":"record","name":"R","fields":[{"name":"p","type":["null",{"type":"record","name":"Card","fields":[{"name":"number","type":"int"}]}]}]}""", Partial, "TypeMismatch@$.p[1:Card].number", ""),

        // Recursion: a recursive record is walked once (Java's IntList and LongList rows).
        ("""{"type":"record","name":"List","fields":[{"name":"head","type":"int"},{"name":"tail","type":["null","List"]}]}""", """{"type":"record","name":"List","fields":[{"name":"head","type":"long"},{"name":"tail","type":["null","List"]}]}""", Compatible, "", ""),
        ("""{"type":"record","name":"List","fields":[{"name":"head","type":"long"},{"name":"tail","type":["null","List"]}]}""", """{"type":"record","name":"List","fields":[{"name":"head","type":"int"},{"name":"tail","type":["null","List"]}]}""", Incompatible, "TypeMismatch@$.head", ""),
    ];

    private static IEnumerable<(string Writer, string Reader, AvroCompatibilityVerdict Verdict, string Issues, string Warnings)> LogicalTypes() =>
    [
        // Logical types don't take part in resolution; changes are warnings.
        ("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""", """{"type":"bytes","logicalType":"decimal","precision":5,"scale":4}""", Compatible, "", "DecimalChanged@$"),
        ("""{"type":"bytes","logicalType":"decimal","precision":9,"scale":2}""", """{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""", Compatible, "", "DecimalChanged@$"),
        ("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""", """{"type":"bytes","logicalType":"decimal","precision":9,"scale":2}""", Compatible, "", ""),
        ("""{"type":"int","logicalType":"date"}""", """{"type":"int","logicalType":"time-millis"}""", Compatible, "", "LogicalTypeChanged@$"),
        ("""{"type":"long","logicalType":"timestamp-millis"}""", """{"type":"long","logicalType":"timestamp-micros"}""", Compatible, "", "LogicalTypeChanged@$"),
        ("""{"type":"long","logicalType":"timestamp-millis"}""", "\"long\"", Compatible, "", "LogicalTypeChanged@$"),
        ("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""", """{"type":"fixed","name":"D","size":8,"logicalType":"decimal","precision":5,"scale":2}""", Incompatible, "TypeMismatch@$", ""),
        ("""{"type":"string","logicalType":"uuid"}""", """{"type":"fixed","name":"U","size":16,"logicalType":"uuid"}""", Incompatible, "TypeMismatch@$", ""),
    ];

    [Test]
    [MethodDataSource(nameof(Pairs))]
    public async Task Check_GivesTheVerdictIssuesAndWarnings(string writer, string reader, AvroCompatibilityVerdict verdict, string issues, string warnings)
    {
        var result = AvroSchemaCompatibility.Check(AvroSchema.Parse(writer), AvroSchema.Parse(reader));

        await Assert.That(result.Verdict).IsEqualTo(verdict);
        await Assert.That(Describe(result.Issues)).IsEqualTo(issues);
        await Assert.That(Describe(result.Warnings)).IsEqualTo(warnings);
        await Assert.That(result.IsCompatible).IsEqualTo(verdict == Compatible);
    }

    /// <summary>
    /// The check and the readers can't drift apart: an incompatible pair fails GenericDatumReader.Create and the
    /// generated-code plan, unless every failure is in a writer union branch or an enum symbol (which the readers
    /// defer to read time, though no value can be read); a compatible or partial pair builds both.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Pairs))]
    public async Task Check_AgreesWithTheResolvingReaders(string writer, string reader, AvroCompatibilityVerdict verdict, string issues, string warnings)
    {
        _ = (issues, warnings);
        var (writerSchema, readerSchema) = (AvroSchema.Parse(writer), AvroSchema.Parse(reader));
        var result = AvroSchemaCompatibility.Check(writerSchema, readerSchema);
        var deferred = result.Issues.All(issue => issue.Kind == AvroCompatibilityKind.MissingEnumSymbols || (issue.Path.Contains('[', StringComparison.Ordinal) && issue.Path.Contains(':', StringComparison.Ordinal)));
        var readerFails = Fails(() => GenericDatumReader.Create(writerSchema, readerSchema, new GenericDatumReaderOptions()));
        // No plan (names that don't match) sends generated code to the resolving reader, which fails.
        var planFails = Fails(() => _ = GenericDatumReader.GetRecordPlan(writerSchema, readerSchema) ?? throw new AvroSchemaException("No plan."));

        var expectFailure = verdict == Incompatible && !deferred;
        await Assert.That(readerFails).IsEqualTo(expectFailure);
        if (writerSchema is RecordSchema && readerSchema is RecordSchema)
        {
            await Assert.That(planFails).IsEqualTo(expectFailure);
        }
    }

    [Test]
    public async Task EveryIncompatibility_IsCollected_InOrder()
    {
        // Java's TestSchemaCompatibilityMultiple, in this library's paths: one pair, every kind of failure.
        var writer = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":"long"},
              {"name":"e","type":{"type":"enum","name":"E","symbols":["A","B","C"]}},
              {"name":"f","type":{"type":"fixed","name":"F","size":8}},
              {"name":"n","type":{"type":"record","name":"N1","fields":[]}},
              {"name":"u","type":["int","string","boolean"]},
              {"name":"m","type":{"type":"map","values":"double"}}]}
            """);
        var reader = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":"int"},
              {"name":"e","type":{"type":"enum","name":"E","symbols":["A"]}},
              {"name":"f","type":{"type":"fixed","name":"F","size":4}},
              {"name":"n","type":{"type":"record","name":"N2","fields":[]}},
              {"name":"u","type":["int"]},
              {"name":"m","type":{"type":"map","values":"float"}},
              {"name":"added","type":"string"}]}
            """);

        var result = AvroSchemaCompatibility.Check(writer, reader);

        await Assert.That(result.Verdict).IsEqualTo(Incompatible);
        await Assert.That(Describe(result.Issues)).IsEqualTo(
            "TypeMismatch@$.a; MissingEnumSymbols@$.e; FixedSizeMismatch@$.f; NameMismatch@$.n; MissingUnionBranch@$.u[1:string]; MissingUnionBranch@$.u[2:boolean]; TypeMismatch@$.m{}; MissingDefault@$.added");
    }

    [Test]
    public async Task ANamedTypeUsedTwice_ReportsItsIssueOnce_WithTheOtherPaths()
    {
        var writer = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"home","type":{"type":"record","name":"Address","fields":[{"name":"zip","type":"long"}]}},
              {"name":"work","type":"Address"}]}
            """);
        var reader = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"home","type":{"type":"record","name":"Address","fields":[{"name":"zip","type":"int"}]}},
              {"name":"work","type":"Address"}]}
            """);

        var result = AvroSchemaCompatibility.Check(writer, reader);

        await Assert.That(result.Issues.Count).IsEqualTo(1);
        await Assert.That(result.Issues[0].Path).IsEqualTo("$.home.zip");
        await Assert.That(result.Issues[0].OtherPaths).IsEquivalentTo(new[] { "$.work.zip" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(result.ToString()).Contains("$.home.zip: ").And.Contains("(also at $.work.zip)");
    }

    [Test]
    public async Task AnInvalidDefault_OfAFieldBuiltInCode_IsAnIncompatibility()
    {
        using var badDefault = JsonDocument.Parse("\"not a number\"");
        var writer = new RecordSchema(new SchemaName("R"), []);
        var reader = new RecordSchema(new SchemaName("R"), [new RecordField("a", new PrimitiveSchema(AvroSchemaType.Int), badDefault.RootElement.Clone())]);

        var result = AvroSchemaCompatibility.Check(writer, reader);

        await Assert.That(Describe(result.Issues)).IsEqualTo("InvalidDefault@$.a");
        await Assert.That(Fails(() => GenericDatumReader.Create(writer, reader, new GenericDatumReaderOptions()))).IsTrue();
    }

    [Test]
    public async Task Options_AllowPartialAndStrict()
    {
        var partial = AvroSchemaCompatibility.Check(AvroSchema.Parse(Enum1ABC), AvroSchema.Parse(Enum1AB), new AvroCompatibilityOptions { AllowPartial = true });
        var decimalWriter = AvroSchema.Parse("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""");
        var decimalReader = AvroSchema.Parse("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":4}""");
        var lenient = AvroSchemaCompatibility.Check(decimalWriter, decimalReader);
        var strict = AvroSchemaCompatibility.Check(decimalWriter, decimalReader, new AvroCompatibilityOptions { Strict = true });

        await Assert.That(partial.Verdict).IsEqualTo(Partial);
        await Assert.That(partial.IsCompatible).IsTrue();
        await Assert.That(lenient.IsCompatible).IsTrue();
        await Assert.That(strict.Verdict).IsEqualTo(Incompatible);
        await Assert.That(strict.IsCompatible).IsFalse();
        await Assert.That(Describe(strict.Warnings)).IsEqualTo("DecimalChanged@$");
    }

    [Test]
    public async Task ThrowIfIncompatible_ThrowsWithEveryIssue()
    {
        var result = AvroSchemaCompatibility.Check(
            AvroSchema.Parse("""{"type":"record","name":"R","fields":[]}"""),
            AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}"""));

        var ex = Assert.Throws<AvroSchemaException>(result.ThrowIfIncompatible);

        await Assert.That(ex.Message).StartsWith("Incompatible.").And.Contains("$.a: ").And.Contains("$.b: ");
        AvroSchemaCompatibility.Check(AvroSchema.Parse("\"int\""), AvroSchema.Parse("\"long\"")).ThrowIfIncompatible();
    }

    [Test]
    [Arguments(AvroCompatibilityLevel.None, "")]
    [Arguments(AvroCompatibilityLevel.Backward, "B2")]
    [Arguments(AvroCompatibilityLevel.BackwardTransitive, "B0 B1 B2")]
    [Arguments(AvroCompatibilityLevel.Forward, "F2")]
    [Arguments(AvroCompatibilityLevel.ForwardTransitive, "F0 F1 F2")]
    [Arguments(AvroCompatibilityLevel.Full, "B2 F2")]
    [Arguments(AvroCompatibilityLevel.FullTransitive, "B0 F0 B1 F1 B2 F2")]
    public async Task Levels_CheckTheVersionsAndDirectionsConfluentDefines(AvroCompatibilityLevel level, string pairs)
    {
        var versions = new[] { "\"int\"", "\"int\"", "\"int\"" }.Select(json => AvroSchema.Parse(json)).ToArray();

        var report = AvroSchemaCompatibility.Check(AvroSchema.Parse("\"int\""), versions, level);

        await Assert.That(string.Join(' ', report.Checks.Select(c => $"{c.Direction.ToString()[0]}{c.Version}"))).IsEqualTo(pairs);
        await Assert.That(report.IsCompatible).IsTrue();
    }

    [Test]
    public async Task ATransitiveLevel_FailsOnAnOlderVersion_ThatTheLatestDoesNotCatch()
    {
        // v0 lacks 'b'; v1 added it with a default, and v2 dropped the default: v2 reads v1's data, not v0's.
        var v0 = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
        var v1 = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int","default":0}]}""");
        var v2 = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}""");

        var latest = AvroSchemaCompatibility.Check(v2, [v0, v1], AvroCompatibilityLevel.Backward);
        var transitive = AvroSchemaCompatibility.Check(v2, [v0, v1], AvroCompatibilityLevel.BackwardTransitive);

        await Assert.That(latest.IsCompatible).IsTrue();
        await Assert.That(transitive.IsCompatible).IsFalse();
        await Assert.That(transitive.Verdict).IsEqualTo(Incompatible);
        await Assert.That(transitive.Checks.Single(c => !c.Result.IsCompatible).Version).IsEqualTo(0);
        await Assert.That(transitive.ToString()).Contains("Reading version 0's data with the new schema: Incompatible.");
    }

    [Test]
    public async Task Arguments_AreChecked()
    {
        var schema = AvroSchema.Parse("\"int\"");

        Assert.Throws<ArgumentNullException>(() => AvroSchemaCompatibility.Check(null!, schema));
        Assert.Throws<ArgumentNullException>(() => AvroSchemaCompatibility.Check(schema, (AvroSchema)null!));
        Assert.Throws<ArgumentNullException>(() => AvroSchemaCompatibility.Check(schema, (IReadOnlyList<AvroSchema>)null!, AvroCompatibilityLevel.Full));
        Assert.Throws<ArgumentOutOfRangeException>(() => AvroSchemaCompatibility.Check(schema, [schema], (AvroCompatibilityLevel)99));
        Assert.Throws<ArgumentException>(() => AvroSchemaCompatibility.Check(schema, [null!], AvroCompatibilityLevel.Backward));
        await Assert.That(AvroSchemaCompatibility.Check(schema, schema).Verdict).IsEqualTo(Compatible);
    }

    private static string Describe(IReadOnlyList<AvroCompatibilityIssue> issues) => string.Join("; ", issues.Select(i => $"{i.Kind}@{i.Path}"));

    private static bool Fails(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (AvroSchemaException)
        {
            return true;
        }
    }
}
