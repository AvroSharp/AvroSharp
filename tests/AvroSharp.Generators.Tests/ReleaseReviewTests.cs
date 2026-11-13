using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>Defects found in the review before 1.1.0, each with the case that showed it.</summary>
public class ReleaseReviewTests
{
    /// <summary>[AvroSerializable] types never get hooks, so their own OnCreated, as in 1.0, still compiles.</summary>
    [Test]
    public async Task ADeclaredType_MayHaveMethodsNamedLikeTheHooks()
    {
        var (_, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.RunTypes("""
            using AvroSharp.Serialization;

            [AvroSerializable]
            public partial class Order
            {
                public int Id { get; set; }

                public void OnCreated() { }

                public void OnDeserialized() { }
            }
            """);

        await Assert.That(generatorDiagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    /// <summary>The keywords "record class" need C# 10; C# 9 gets "record".</summary>
    [Test]
    public async Task RecordTypes_CompileAtCSharp9()
    {
        var sources = CSharpCodeGenerator.Generate(
            [AvroSchema.Parse("""{"type":"record","name":"Line","namespace":"shop","fields":[{"name":"qty","type":"int"}]}""")],
            new CodeGenOptions { TypeKind = CodeGenTypeKind.Record, LanguageVersion = 9 });

        var diagnostics = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.CSharp9, [.. sources.Select(s => s.Text)]).GetDiagnostics();

        await Assert.That(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Single().Text).Contains("public sealed partial record Line");
    }

    /// <summary>A name the file defines itself wins over another file's type of the same simple name in the null namespace.</summary>
    [Test]
    public async Task TheCycleInliner_ResolvesAFilesOwnNamesFirst()
    {
        var set = SchemaFileSet.Parse([
            ("x.avsc", """{"type":"record","name":"X","namespace":"ns","fields":[{"name":"k","type":{"type":"enum","name":"Kind","symbols":["A"]}},{"name":"again","type":"Kind"},{"name":"y","type":["null","Y"]}]}"""),
            ("y.avsc", """{"type":"record","name":"Y","fields":[{"name":"x","type":["null","ns.X"]}]}"""),
            ("z.avsc", """{"type":"enum","name":"Kind","symbols":["OTHER"],"namespace":""}"""),
        ]);

        var x = (RecordSchema)set.SchemasByPath["x.avsc"];
        await Assert.That(set.Errors).IsEmpty();
        await Assert.That(((NamedSchema)x.GetField("again").Schema).FullName).IsEqualTo("ns.Kind");
    }

    /// <summary>A type nested in an inlined definition, also defined identically by the file being parsed, is not inlined twice.</summary>
    [Test]
    public async Task TheCycleInliner_DoesNotInlineATypeTwice()
    {
        var set = SchemaFileSet.Parse([
            ("a.avsc", """{"type":"record","name":"A","namespace":"ns","fields":[{"name":"b","type":["null","B"]},{"name":"again","type":["null","B"]}]}"""),
            ("b.avsc", """{"type":"record","name":"B","namespace":"ns","fields":[{"name":"k","type":{"type":"enum","name":"Kind","symbols":["A"]}},{"name":"a","type":["null","A"]},{"name":"same","type":"Kind"}]}"""),
        ]);

        await Assert.That(set.Errors).IsEmpty();
    }

    /// <summary>A reference written as an object, {"type": "ns.B"}, is inlined as a whole.</summary>
    [Test]
    public async Task TheCycleInliner_InlinesAReferenceWrittenAsAnObject()
    {
        var set = SchemaFileSet.Parse([
            ("a.avsc", """{"type":"record","name":"A","namespace":"ns","fields":[{"name":"b","type":["null",{"type":"ns.B"}]}]}"""),
            ("b.avsc", """{"type":"record","name":"B","namespace":"ns","fields":[{"name":"a","type":["null","A"]}]}"""),
        ]);

        await Assert.That(set.Errors).IsEmpty();
    }
}
