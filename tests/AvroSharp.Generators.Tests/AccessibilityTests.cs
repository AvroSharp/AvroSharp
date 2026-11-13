using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AvroSharp.Generators.Tests;

/// <summary>AvroSharpAccessibility and AvroSharpSealedTypes, for the project and per file (#18).</summary>
public class AccessibilityTests
{
    private const string Order = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID"]}},
          {"name":"sku","type":{"type":"fixed","name":"Sku","size":2}},
          {"name":"line","type":["null","shop.Line"]}]}
        """;

    private const string Line = """{"type":"record","name":"Line","namespace":"shop","fields":[{"name":"qty","type":"int"}]}""";

    [Test]
    public async Task InternalTypes_AreInternal_AndCompile()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Order), ("line.avsc", Line)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpAccessibility"] = "internal" });

        var code = string.Join("\n", sources.Select(s => s.SourceText.ToString()));
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(code).Contains("internal sealed partial class Order");
        await Assert.That(code).Contains("internal enum Status");
        await Assert.That(code).Contains("internal sealed partial class Sku");
        await Assert.That(code).DoesNotContain("public sealed partial class");
    }

    [Test]
    public async Task UnsealedTypes_CanBeDerivedFrom()
    {
        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Order), ("line.avsc", Line)],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpSealedTypes"] = "false" });
        var compilation = GeneratorHarness.CreateCompilation(true, [.. sources.Select(s => s.SourceText.ToString()), "public sealed class Rush : shop.Order { }"]);

        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Single(s => s.HintName.EndsWith("Order.g.cs", StringComparison.Ordinal)).SourceText.ToString()).Contains("public partial class Order");
        await Assert.That(compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task APublicTypeUsingAnInternalOne_IsReported()
    {
        var metadata = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["line.avsc"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpAccessibility"] = "internal" },
        };

        var (sources, diagnostics, _) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Order), ("line.avsc", Line)], new Dictionary<string, string>(StringComparer.Ordinal), metadata);

        var diagnostic = diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("AVROGEN003");
        await Assert.That(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Contains("The public type 'shop.Order' has the field 'line' of the internal type 'shop.Line'");
        await Assert.That(sources).IsEmpty();
    }

    [Test]
    public async Task AnInternalFile_InAPublicProject_CompilesWhenNothingPublicUsesIt()
    {
        var metadata = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["order.avsc"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpAccessibility"] = "internal" },
            ["line.avsc"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpAccessibility"] = "internal" },
        };

        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("order.avsc", Order), ("line.avsc", Line), ("other.avsc", """{"type":"record","name":"Other","fields":[{"name":"x","type":"int"}]}""")],
            new Dictionary<string, string>(StringComparer.Ordinal), metadata);

        var code = string.Join("\n", sources.Select(s => s.SourceText.ToString()));
        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(code).Contains("internal sealed partial class Order");
        await Assert.That(code).Contains("public sealed partial class Other");
    }
}
