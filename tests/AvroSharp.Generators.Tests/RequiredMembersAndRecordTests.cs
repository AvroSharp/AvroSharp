using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>Required members and record classes (#15): CodeGenOptions.RequiredMembers and TypeKind, and their MSBuild properties.</summary>
public class RequiredMembersAndRecordTests
{
    private const string OrderSchema = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"id","type":"long"},
          {"name":"customer","type":"string"},
          {"name":"line","type":{"type":"record","name":"Line","fields":[{"name":"sku","type":"string"},{"name":"qty","type":"int","default":1}]}},
          {"name":"tags","type":{"type":"array","items":"string"}},
          {"name":"note","type":["null","string"],"default":null},
          {"name":"status","type":"string","default":"new"}]}
        """;

    // What a .NET 10 project and a .NET Standard 2.0 one define.
    private static readonly string[] s_net10 =
        ["NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"];

    private static readonly string[] s_netStandard = ["NETSTANDARD", "NETSTANDARD2_0"];

    [Test]
    public async Task RequiredMembers_AreFieldsWithoutDefaults_OnNet7AndLater()
    {
        var code = Generate(new CodeGenOptions { RequiredMembers = true });

        await Assert.That(code).Contains("public required long Id { get; set; }");
        await Assert.That(code).Contains("public required string Customer { get; set; }");
        await Assert.That(code).Contains("public required global::shop.Line Line { get; set; }");
        await Assert.That(code).Contains("public string? Note { get; set; }");
        await Assert.That(code).Contains("public string Status { get; set; }");
        await Assert.That(code).Contains("[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
    }

    [Test]
    public async Task RequiredMembers_AreCheckedByTheCompiler_AndTheCodeRuns()
    {
        var sources = GenerateSources(new CodeGenOptions { RequiredMembers = true });
        const string Missing = "class Use { shop.Order Make() => new shop.Order { Id = 1 }; }";
        const string Complete = """
            class Use
            {
                public static byte[] RoundTrip() => shop.Order.FromAvroBytes(new shop.Order
                {
                    Id = 1, Customer = "Ada", Line = new shop.Line { Sku = "x" }, Tags = [],
                }.ToAvroBytes()).ToAvroBytes();
            }
            """;

        var missing = Compile(sources, s_net10, Missing).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var complete = Compile(sources, s_net10, Complete).Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();

        await Assert.That(missing.Select(d => d.Id).Distinct().ToList()).IsEquivalentTo(["CS9035"], TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(string.Join("\n", missing.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))).Contains("Customer");
        await Assert.That(complete.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task RequiredMembers_OnOlderTargets_ArePlainProperties()
    {
        // A .NET Standard project has no RequiredMemberAttribute: the same code compiles, without required, at C# 7.3 too.
        var sources = GenerateSources(new CodeGenOptions { RequiredMembers = true, NullableAnnotations = false, LanguageVersion = 11 });
        var older = GenerateSources(new CodeGenOptions { RequiredMembers = true, NullableAnnotations = false, LanguageVersion = 7 });

        var diagnostics = Compile(sources, s_netStandard, "class Use { shop.Order Make() => new shop.Order(); }", LanguageVersion.CSharp11);
        var olderDiagnostics = Compile(older, s_netStandard, "class Use { shop.Order Make() => new shop.Order(); }", LanguageVersion.CSharp7_3);

        await Assert.That(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(olderDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(string.Join("\n", older.Select(s => s.Text))).DoesNotContain("required");
    }

    [Test]
    public async Task RecordTypes_AreRecordClasses_WithValueEquality()
    {
        var sources = GenerateSources(new CodeGenOptions { TypeKind = CodeGenTypeKind.Record });
        const string Use = """
            public static class Use
            {
                public static bool Equal()
                {
                    var a = new shop.Line { Sku = "x", Qty = 2 };
                    var b = shop.Line.FromAvroBytes(a.ToAvroBytes());
                    var c = b with { Qty = 3 };
                    return a == b && a != c && c.Qty == 3;
                }
            }
            """;

        var assembly = Load(sources, Use);
        var equal = (bool)assembly.GetType("Use")!.GetMethod("Equal")!.Invoke(null, null)!;

        await Assert.That(string.Join("\n", sources.Select(s => s.Text))).Contains("public sealed partial record class Order");
        await Assert.That(equal).IsTrue();
    }

    [Test]
    public async Task RecordTypes_NeedCSharp9()
    {
        var ex = Assert.Throws<ArgumentException>(() => GenerateSources(new CodeGenOptions { TypeKind = CodeGenTypeKind.Record, LanguageVersion = 8 }));

        await Assert.That(ex.Message).Contains("C# 9");
    }

    [Test]
    public async Task MSBuildProperties_SetBothOptions_AndRejectOtherValues()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("order.avsc", OrderSchema)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpRequiredMembers"] = "true", ["AvroSharpTypeKind"] = "Record" });
        var (_, wrong, _) = GeneratorHarness.RunWithProperties(
            [("order.avsc", OrderSchema)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpRequiredMembers"] = "yes", ["AvroSharpTypeKind"] = "struct" });

        var code = string.Join("\n", sources.Select(s => s.SourceText.ToString()));
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(code).Contains("public required long Id");
        await Assert.That(code).Contains("partial record class Order");
        await Assert.That(wrong.Select(d => d.Id).ToList()).IsEquivalentTo(["AVROGEN006", "AVROGEN006"], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    private static string Generate(CodeGenOptions options) => string.Join("\n", GenerateSources(options).Select(s => s.Text));

    private static IReadOnlyList<GeneratedSource> GenerateSources(CodeGenOptions options) =>
        CSharpCodeGenerator.Generate([AvroSchema.Parse(OrderSchema)], options);

    private static System.Collections.Immutable.ImmutableArray<Diagnostic> Compile(IReadOnlyList<GeneratedSource> sources, string[] symbols, string use, LanguageVersion version = LanguageVersion.Latest) =>
        GeneratorHarness.CreateCompilation(true, false, version, symbols, [.. sources.Select(s => s.Text), use]).GetDiagnostics();

    private static System.Reflection.Assembly Load(IReadOnlyList<GeneratedSource> sources, string use)
    {
        var compilation = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.Latest, s_net10, [.. sources.Select(s => s.Text), use]);
        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return System.Reflection.Assembly.Load(image.ToArray());
    }
}
