using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Enums;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// The generator's MSBuild properties (#134): the same settings and values as <see cref="CodeGenOptions"/> and the
/// CLI, with <c>AvroSharpNamespaceMap</c>, and a warning (AVROGEN006) for a value it does not recognize.
/// </summary>
public class MSBuildPropertyTests
{
    private const string Schema = """
        {"type":"record","name":"Order","namespace":"com.example.shop","fields":[{"name":"order_id","type":"long"}]}
        """;

    [Test]
    public async Task AvroSharpNamespaceMap_MapsNamespaces_AsTheCliDoes()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Schema)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpNamespaceMap"] = " com.example : Example ; org.other:Other" });

        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Single().SourceText.ToString()).Contains("namespace Example.shop");
    }

    /// <summary>
    /// The compiler reads the properties from the .editorconfig file that the SDK writes, where ';' starts a comment:
    /// every entry after the first was lost. build/AvroSharp.Generators.targets passes the ';' on as ','.
    /// </summary>
    [Test]
    public async Task AvroSharpNamespaceMap_KeepsEveryEntry_ThroughTheEditorConfigFile()
    {
        const string Customer = """
            {"type":"record","name":"Customer","namespace":"com.example.crm","fields":[{"name":"id","type":"long"}]}
            """;
        var value = ThroughEditorConfig("com.example.shop:Shop.Orders,com.example.crm:Shop.Customers");

        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Schema), ("customer.avsc", Customer)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpNamespaceMap"] = value });

        await Assert.That(ThroughEditorConfig("com.example.shop:Shop.Orders;com.example.crm:Shop.Customers")).IsEqualTo("com.example.shop:Shop.Orders");
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        var code = string.Join("\n", sources.Select(s => s.SourceText.ToString()));
        await Assert.That(code).Contains("namespace Shop.Orders");
        await Assert.That(code).Contains("namespace Shop.Customers");
    }

    // The value the compiler gets for a property the SDK writes to the project's .editorconfig file.
    private static string ThroughEditorConfig(string value)
    {
        var text = $"is_global = true\nbuild_property.AvroSharpNamespaceMap = {value}\n";
        var config = AnalyzerConfig.Parse(Microsoft.CodeAnalysis.Text.SourceText.From(text), "/project/obj/project.GeneratedMSBuildEditorConfig.editorconfig");
        var options = AnalyzerConfigSet.Create(ImmutableArray.Create(config)).GlobalConfigOptions.AnalyzerOptions;
        return options.Single(option => option.Key.EndsWith("AvroSharpNamespaceMap", StringComparison.OrdinalIgnoreCase)).Value;
    }

    [Test]
    public async Task Values_AreCaseInsensitive()
    {
        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Schema)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpPropertyNames"] = "Avro", ["AvroSharpLogicalTypes"] = "RAW", ["AvroSharpApacheCompatible"] = "False" });

        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Single().SourceText.ToString()).Contains(" order_id ");
    }

    /// <summary>A typo used to pass silently; now it is a warning, and the default is used.</summary>
    [Test]
    public async Task UnrecognizedValues_AreWarnings_AndTheDefaultsAreUsed()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Schema)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AvroSharpLogicalTypes"] = "rwa",
                ["AvroSharpPropertyNames"] = "camel",
                ["AvroSharpApacheCompatible"] = "yes",
                ["AvroSharpNamespaceMap"] = "nocolon;com.example:Example;com.example:Other",
            });

        var warnings = diagnostics.Where(d => string.Equals(d.Id, "AVROGEN006", StringComparison.Ordinal)).ToList();
        await Assert.That(warnings.All(d => d.Severity == DiagnosticSeverity.Warning)).IsTrue();
        await Assert.That(warnings.Select(d => d.GetMessage(CultureInfo.InvariantCulture))).IsEquivalentTo(
            new[]
            {
                "AvroSharpLogicalTypes is 'rwa', which is not native or raw; native is used.",
                "AvroSharpApacheCompatible is 'yes', which is not true or false; false is used.",
                "AvroSharpPropertyNames is 'camel', which is not pascal or avro; the default is used.",
                "AvroSharpNamespaceMap has 'nocolon', which is not avro.namespace:CSharp.Namespace (names separated by dots, on both sides of the colon); it is ignored.",
                "AvroSharpNamespaceMap maps 'com.example' more than once; the first mapping is used.",
            },
            CollectionOrdering.Matching);
        await Assert.That(diagnostics.Count).IsEqualTo(warnings.Count);

        // The defaults, and the first mapping: PascalCase properties in the Example namespace.
        var code = sources.Single().SourceText.ToString();
        await Assert.That(code).Contains("namespace Example.shop");
        await Assert.That(code).Contains(" OrderId ");
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task NullableAnnotations_BelowCSharp8_AreRejected()
    {
        var schema = (Schemas.RecordSchema)Schemas.AvroSchema.Parse(Schema);

        var ex = Assert.Throws<ArgumentException>(() => CSharpCodeGenerator.Generate([schema], new CodeGenOptions { LanguageVersion = 7 }));
        var tooOld = Assert.Throws<ArgumentException>(() => CSharpCodeGenerator.Generate([schema], new CodeGenOptions { LanguageVersion = 6, NullableAnnotations = false }));

        await Assert.That(ex.Message).StartsWith("Nullable annotations need C# 8 or later, and the language version is 7: set NullableAnnotations to false.");
        await Assert.That(tooOld.Message).StartsWith("The language version 6 is below the lowest supported, C# 7.");
        await Assert.That(CSharpCodeGenerator.Generate([schema], new CodeGenOptions { LanguageVersion = 7, NullableAnnotations = false }).Count).IsEqualTo(1);
    }
}
