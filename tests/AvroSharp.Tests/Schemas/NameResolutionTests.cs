using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>The "Names" rules of the specification.</summary>
public class NameResolutionTests
{
    [Test]
    public async Task Namespace_ComesFromTheNamespaceAttribute()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","namespace":"a.b","fields":[]}""");
        await Assert.That(schema.Name.Name).IsEqualTo("R");
        await Assert.That(schema.Name.Namespace).IsEqualTo("a.b");
        await Assert.That(schema.FullName).IsEqualTo("a.b.R");
    }

    [Test]
    public async Task DottedName_IsAFullNameAndIgnoresTheNamespaceAttribute()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"x.y.R","namespace":"a.b","fields":[]}""");
        await Assert.That(schema.FullName).IsEqualTo("x.y.R");
        await Assert.That(schema.Name.Namespace).IsEqualTo("x.y");
    }

    [Test]
    public async Task NestedNamedTypes_InheritTheEnclosingNamespace()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Outer","namespace":"ns","fields":[
              {"name":"e","type":{"type":"enum","name":"E","symbols":["A"]}},
              {"name":"f","type":{"type":"fixed","name":"F","namespace":"other","size":2}},
              {"name":"g","type":{"type":"record","name":"G","namespace":"","fields":[]}}
            ]}
            """);
        await Assert.That(((NamedSchema)schema.Fields[0].Schema).FullName).IsEqualTo("ns.E");
        await Assert.That(((NamedSchema)schema.Fields[1].Schema).FullName).IsEqualTo("other.F");
        await Assert.That(((NamedSchema)schema.Fields[2].Schema).FullName).IsEqualTo("G");
        await Assert.That(((NamedSchema)schema.Fields[2].Schema).Name.Namespace).IsNull();
    }

    [Test]
    public async Task SimpleNameReference_ResolvesInTheEnclosingNamespace()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","namespace":"ns","fields":[
              {"name":"a","type":{"type":"enum","name":"E","symbols":["X"]}},
              {"name":"b","type":"E"},
              {"name":"c","type":"ns.E"}
            ]}
            """);
        await Assert.That(schema.Fields[1].Schema).IsSameReferenceAs(schema.Fields[0].Schema);
        await Assert.That(schema.Fields[2].Schema).IsSameReferenceAs(schema.Fields[0].Schema);
    }

    [Test]
    public async Task SimpleNameReference_FallsBackToTheNullNamespace()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":{"type":"enum","name":"E","symbols":["X"]}},
              {"name":"b","type":{"type":"record","name":"Inner","namespace":"ns","fields":[{"name":"e","type":"E"}]}}
            ]}
            """);
        var inner = (RecordSchema)schema.Fields[1].Schema;
        await Assert.That(inner.Fields[0].Schema).IsSameReferenceAs(schema.Fields[0].Schema);
    }

    [Test]
    public async Task TypeAttributeWithAName_RefersToThatType()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":{"type":"fixed","name":"F","size":4}},
              {"name":"b","type":{"type":"F"}}
            ]}
            """);
        await Assert.That(schema.Fields[1].Schema).IsSameReferenceAs(schema.Fields[0].Schema);
    }

    [Test]
    public async Task RecursiveReference_ResolvesToTheRecordBeingDefined()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]}]}
            """);
        var union = (UnionSchema)schema.Fields[0].Schema;
        await Assert.That(union.Branches[1]).IsSameReferenceAs(schema);
    }

    [Test]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a","type":"Missing"}]}""", "$.fields[0].type")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a","type":"Later"},{"name":"b","type":{"type":"fixed","name":"Later","size":1}}]}""", "$.fields[0].type")]
    public async Task UndefinedName_IsRejectedWithItsLocation(string json, string path)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json));
        await Assert.That(ex.Path).IsEqualTo(path);
        await Assert.That(ex.Reason!).Contains("is not a defined type");
    }

    [Test]
    public async Task DuplicateName_IsRejected()
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":{"type":"fixed","name":"F","size":1}},
              {"name":"b","type":{"type":"enum","name":"F","symbols":["X"]}}
            ]}
            """));
        await Assert.That(ex.Reason!).Contains("'F' is already defined");
        await Assert.That(ex.Path).IsEqualTo("$.fields[1].type");
    }

    [Test]
    [Arguments("int")]
    [Arguments("string")]
    [Arguments("null")]
    public async Task PrimitiveTypeName_CannotNameANamedType(string name)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse($$"""{"type":"fixed","name":"{{name}}","size":1}"""));
        await Assert.That(ex.Reason!).Contains("primitive type name");
    }

    [Test]
    [Arguments("1abc")]
    [Arguments("a-b")]
    [Arguments("")]
    [Arguments("a.")]
    [Arguments(".a")]
    [Arguments("a..b")]
    public async Task InvalidName_IsRejected(string name)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse($$"""{"type":"fixed","name":"{{name}}","size":1}"""));
        await Assert.That(ex.Path).IsEqualTo("$.name");
    }

    [Test]
    public async Task InvalidNames_AreAcceptedWhenValidationIsDisabled()
    {
        var options = new AvroSchemaParseOptions { ValidateNames = false };
        var schema = (RecordSchema)AvroSchema.Parse(
            """{"type":"record","name":"my-record","namespace":"com.1bad","fields":[{"name":"field-1","type":{"type":"enum","name":"E","symbols":["a-b"]}}]}""",
            options);
        await Assert.That(schema.FullName).IsEqualTo("com.1bad.my-record");
        await Assert.That(schema.Fields[0].Name).IsEqualTo("field-1");
        await Assert.That(((EnumSchema)schema.Fields[0].Schema).Symbols[0]).IsEqualTo("a-b");
    }

    [Test]
    public async Task Aliases_AreResolvedRelativeToTheNamespace()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","namespace":"ns","aliases":["Old","other.Older"],"fields":[]}""");
        await Assert.That(schema.Aliases[0].FullName).IsEqualTo("ns.Old");
        await Assert.That(schema.Aliases[1].FullName).IsEqualTo("other.Older");
    }

    [Test]
    public async Task SchemaName_ComparesByFullName()
    {
        await Assert.That(new SchemaName("R", "a.b")).IsEqualTo(new SchemaName("a.b.R"));
        await Assert.That(new SchemaName("R", "") == new SchemaName("R")).IsTrue();
        await Assert.That(new SchemaName("R", "a") != new SchemaName("R", "b")).IsTrue();
    }
}
