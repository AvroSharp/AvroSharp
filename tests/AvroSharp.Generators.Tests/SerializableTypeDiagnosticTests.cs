using System.Linq;
using System.Threading.Tasks;

namespace AvroSharp.Generators.Tests;

/// <summary>The attribute-driven generator's diagnostics (#31): each is reported at the code, and nothing is generated for the type.</summary>
public class SerializableTypeDiagnosticTests
{
    private const string Usings = "using System; using System.Collections.Generic; using AvroSharp.Serialization; namespace D; ";

    [Test]
    [Arguments("[AvroSerializable] public class T { public int A { get; set; } }", "AVROGEN101")]
    [Arguments("[AvroSerializable] public abstract partial class T { public int A { get; set; } }", "AVROGEN101")]
    [Arguments("[AvroSerializable] public partial class T { public short A { get; set; } }", "AVROGEN102")]
    [Arguments("[AvroSerializable] public partial class T { public Dictionary<int, string> A { get; set; } = new(); }", "AVROGEN102")]
    [Arguments("[AvroSerializable] public partial class T { public TimeSpan A { get; set; } }", "AVROGEN102")]
    [Arguments("[AvroSerializable] public partial class T { public decimal A { get; set; } }", "AVROGEN103")]
    [Arguments("[AvroSerializable] public partial class T { [AvroName(\"not-valid\")] public int A { get; set; } }", "AVROGEN104")]
    [Arguments("[AvroSerializable(Name = \"1st\")] public partial class T { public int A { get; set; } }", "AVROGEN104")]
    [Arguments("[AvroSerializable] public partial class T { public int A { get; set; } [AvroName(\"A\")] public int B { get; set; } }", "AVROGEN105")]
    [Arguments("[AvroSerializable] public partial class T { [AvroDefault(\"{nope\")] public int A { get; set; } }", "AVROGEN106")]
    [Arguments("[AvroSerializable] public partial class T { [AvroDefault(\"\\\"text\\\"\")] public int A { get; set; } }", "AVROGEN106")]
    [Arguments("[AvroSerializable] public partial class T<X> { public int A { get; set; } }", "AVROGEN107")]
    [Arguments("public partial class Outer { [AvroSerializable] public partial class T { public int A { get; set; } } }", "AVROGEN107")]
    [Arguments("[AvroSerializable] public partial record T(int A);", "AVROGEN108")]
    [Arguments("[AvroSerializable] public partial class T { [AvroField(Order = 1)] public int A { get; set; } public int B { get; set; } }", "AVROGEN109")]
    [Arguments("[AvroSerializable] public partial class T { [AvroUnion(typeof(U))] public string A { get; set; } = \"\"; } [AvroSerializable] public partial class U { public int X { get; set; } }", "AVROGEN110")]
    [Arguments("[AvroSerializable] public partial class T { public U A { get; set; } = new(); } public class U { public int X { get; set; } }", "AVROGEN111")]
    [Arguments("[AvroSerializable] public partial class T { [AvroDecimal(10, 2)] public int A { get; set; } }", "AVROGEN112")]
    [Arguments("[AvroSerializable] public partial class T { public U A { get; set; } = new(); } [AvroSerializable(Name = \"T\")] public partial class U { public int X { get; set; } }", "AVROGEN113")]
    [Arguments("[AvroSerializable] public partial class T { public DateTime A { get; set; } }", "AVROGEN114")]
    [Arguments("[AvroSerializable] public partial class T { public int A { get; init; } }", "AVROGEN115")]
    [Arguments("public enum E { A = 1, B = 2 } [AvroSerializable] public partial class T { public E A { get; set; } }", "AVROGEN116")]
    [Arguments("[AvroSerializable] public partial class T { public int Schema { get; set; } }", "AVROGEN117")]
    public async Task AProblem_IsReported_WithItsId(string code, string id)
    {
        var (_, generatorDiagnostics, _) = GeneratorHarness.RunTypes(Usings + code);

        await Assert.That(generatorDiagnostics.Select(d => d.Id)).Contains(id);
        // Rebuilt from cached data, the location is a file path and span (external), with the line of the code.
        await Assert.That(generatorDiagnostics.Where(d => string.Equals(d.Id, id, System.StringComparison.Ordinal)).All(d => d.Location.Kind != Microsoft.CodeAnalysis.LocationKind.None && d.Location.GetLineSpan().IsValid)).IsTrue();
    }

    [Test]
    public async Task AnError_GeneratesNothing_ButAWarningDoes()
    {
        var (errorSources, _, _) = GeneratorHarness.RunTypes(Usings + "[AvroSerializable] public partial class T { public decimal A { get; set; } }");
        var (warningSources, warnings, _) = GeneratorHarness.RunTypes(Usings + "[AvroSerializable] public partial class T { [AvroDecimal(10, 2)] public int A { get; set; } }");

        await Assert.That(errorSources).IsEmpty();
        await Assert.That(warnings.Single().Id).IsEqualTo("AVROGEN112");
        await Assert.That(warningSources.Length).IsEqualTo(1);
    }
}
