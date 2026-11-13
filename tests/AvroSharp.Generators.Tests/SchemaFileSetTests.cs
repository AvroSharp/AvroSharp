using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

namespace AvroSharp.Generators.Tests;

/// <summary>SchemaFileSet: files that refer to each other's types, in any order and in cycles (#16).</summary>
public class SchemaFileSetTests
{
    [Test]
    public async Task FilesThatNeedEachOther_Parse_AndEachTypeIsDefinedInItsOwnFile()
    {
        var set = SchemaFileSet.Parse([
            ("a.avsc", """{"type":"record","name":"A","namespace":"x.y","fields":[{"name":"b","type":["null","x.z.B"]}]}"""),
            ("b.avsc", """{"type":"record","name":"B","namespace":"x.z","fields":[{"name":"a","type":["null","x.y.A"]}]}"""),
        ]);

        await Assert.That(set.Errors).IsEmpty();
        await Assert.That(set.DefinedIn["x.y.A"]).IsEqualTo("a.avsc");
        await Assert.That(set.DefinedIn["x.z.B"]).IsEqualTo("b.avsc");
        var a = (RecordSchema)set.SchemasByPath["a.avsc"];
        var b = (RecordSchema)((UnionSchema)a.GetField("b").Schema).Branches[1];
        await Assert.That(((UnionSchema)b.GetField("a").Schema).Branches[1]).IsSameReferenceAs(a);
    }

    [Test]
    public async Task ACycleThroughTheNullNamespace_ResolvesNamesAsInItsOwnFile()
    {
        // Q has no namespace; inlined into ns.P it keeps none, and its unqualified reference to Leaf stays in the null
        // namespace, where leaf.avsc defines it.
        var set = SchemaFileSet.Parse([
            ("p.avsc", """{"type":"record","name":"P","namespace":"ns","fields":[{"name":"q","type":["null","Q"]}]}"""),
            ("q.avsc", """{"type":"record","name":"Q","fields":[{"name":"p","type":["null","ns.P"]},{"name":"leaf","type":"Leaf"}]}"""),
            ("leaf.avsc", """{"type":"enum","name":"Leaf","symbols":["X"]}"""),
        ]);

        await Assert.That(set.Errors).IsEmpty();
        await Assert.That(set.DefinedIn.Keys.Order(System.StringComparer.Ordinal).ToArray()).IsEquivalentTo(["Leaf", "Q", "ns.P"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(set.DefinedIn["Q"]).IsEqualTo("q.avsc");
    }

    [Test]
    public async Task ACycleOfThreeFiles_Parses()
    {
        var set = SchemaFileSet.Parse([
            ("1.avsc", """{"type":"record","name":"One","namespace":"c","fields":[{"name":"two","type":["null","Two"]}]}"""),
            ("2.avsc", """{"type":"record","name":"Two","namespace":"c","fields":[{"name":"three","type":["null","Three"]}]}"""),
            ("3.avsc", """{"type":"record","name":"Three","namespace":"c","fields":[{"name":"one","type":["null","One"]}]}"""),
        ]);

        await Assert.That(set.Errors).IsEmpty();
        await Assert.That(set.SchemasByPath.Count).IsEqualTo(3);
    }

    [Test]
    public async Task AnUndefinedName_IsStillReportedInItsOwnFile()
    {
        var set = SchemaFileSet.Parse([
            ("a.avsc", """{"type":"record","name":"A","fields":[{"name":"b","type":["null","B"]}]}"""),
            ("b.avsc", "{\"type\":\"record\",\"name\":\"B\",\"fields\":[\n  {\"name\":\"a\",\"type\":[\"null\",\"A\"]},\n  {\"name\":\"c\",\"type\":\"Missing\"}]}"),
        ]);

        await Assert.That(set.Errors.Keys.Order(System.StringComparer.Ordinal).ToArray()).IsEquivalentTo(["a.avsc", "b.avsc"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var error = set.Errors["b.avsc"];
        await Assert.That(error.Message).Contains("'A' is not a defined type");
        await Assert.That(error.LineNumber).IsEqualTo(2);
    }
}
