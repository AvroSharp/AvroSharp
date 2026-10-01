using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using TUnit.Assertions.Enums;

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

    private const string Address = """{"type":"record","name":"Address","namespace":"geo","doc":"first","fields":[{"name":"city","type":"string"}]}""";

    private const string AddressWithOtherDoc = """{"type":"record","name":"Address","namespace":"geo","doc":"second","fields":[{"name":"city","type":"string"}]}""";

    [Test]
    public async Task IdenticalRedefinitionInALaterParse_IsRejectedByDefault()
    {
        var parser = new AvroSchemaParser();
        parser.Parse(Address);

        var ex = Assert.Throws<AvroSchemaException>(() => parser.Parse("""{"type":"record","name":"Holder","fields":[{"name":"a","type":""" + Address + "}]}"));

        await Assert.That(ex.Reason!).Contains("'geo.Address' is already defined");
    }

    [Test]
    public async Task IdenticalRedefinitionInALaterParse_IsAcceptedWhenAllowed_AndTheFirstDefinitionStays()
    {
        var parser = new AvroSchemaParser(new AvroSchemaParseOptions { AllowIdenticalRedefinitions = true });
        var first = (RecordSchema)parser.Parse(Address);

        // Docs may differ: the canonical form is what must match.
        var holder = (RecordSchema)parser.Parse("""{"type":"record","name":"Holder","fields":[{"name":"a","type":""" + AddressWithOtherDoc + """},{"name":"b","type":"geo.Address"}]}""");

        await Assert.That(holder.Fields[1].Schema.CanonicalForm).IsEqualTo(first.CanonicalForm);
        await Assert.That(ReferenceEquals(parser.NamedSchemas["geo.Address"], first)).IsTrue();
    }

    [Test]
    public async Task DifferentRedefinitionInALaterParse_IsRejectedEvenWhenAllowed()
    {
        var parser = new AvroSchemaParser(new AvroSchemaParseOptions { AllowIdenticalRedefinitions = true });
        parser.Parse(Address);

        var ex = Assert.Throws<AvroSchemaException>(() => parser.Parse("""{"type":"record","name":"Address","namespace":"geo","fields":[{"name":"zip","type":"int"}]}"""));

        await Assert.That(ex.Reason!).Contains("'geo.Address' is already defined differently");
        await Assert.That(parser.NamedSchemas["geo.Address"].CanonicalForm).Contains("city");
    }

    [Test]
    public async Task RedefinitionWithinOneSchema_IsRejectedEvenWhenAllowed()
    {
        var parser = new AvroSchemaParser(new AvroSchemaParseOptions { AllowIdenticalRedefinitions = true });

        var ex = Assert.Throws<AvroSchemaException>(() => parser.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":{"type":"fixed","name":"F","size":1}},
              {"name":"b","type":{"type":"fixed","name":"F","size":1}}
            ]}
            """));

        await Assert.That(ex.Reason!).Contains("'F' is already defined");
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

    /// <summary>An invalid namespace attribute is reported at $.namespace, where it is written, not at $.name.</summary>
    [Test]
    [Arguments("a..b")]
    [Arguments("1a")]
    [Arguments("a.")]
    public async Task InvalidNamespace_IsRejectedAtTheNamespaceAttribute(string @namespace)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse($$"""{"type":"fixed","name":"F","namespace":"{{@namespace}}","size":1}"""));

        await Assert.That(ex.Path).IsEqualTo("$.namespace");
        await Assert.That(ex.Message).Contains($"'{@namespace}' is not a valid Avro namespace.");
    }

    /// <summary>
    /// Field aliases are kept as written and not name-checked, as in Java, whose Field constructor validates the name
    /// but adds aliases unchecked; a schema with such aliases from another library still parses.
    /// </summary>
    [Test]
    public async Task FieldAliases_AreNotNameChecked()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"f","type":"int","aliases":["bad alias","a.b"]}]}""");

        await Assert.That(schema.Fields[0].Aliases).IsEquivalentTo(new[] { "bad alias", "a.b" }, CollectionOrdering.Matching);
    }

    /// <summary>A field alias may equal another field's name; aliases only matter when resolving against a writer.</summary>
    [Test]
    public async Task FieldAlias_MayEqualAnotherFieldsName()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int","aliases":["a"]}]}
            """);

        await Assert.That(schema.Fields[1].Aliases).IsEquivalentTo(new[] { "a" }, CollectionOrdering.Matching);
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

    /// <summary>Enums and fixed types have aliases too: parsed relative to the namespace, and written back.</summary>
    [Test]
    [Arguments("""{"type":"enum","name":"E","namespace":"ns","symbols":["A"],"aliases":["Old","other.Older"]}""")]
    [Arguments("""{"type":"fixed","name":"F","namespace":"ns","size":2,"aliases":["Old","other.Older"]}""")]
    public async Task EnumAndFixedAliases_AreParsedAndWritten(string json)
    {
        var schema = (NamedSchema)AvroSchema.Parse(json);
        var written = schema.ToJson();
        var reparsed = (NamedSchema)AvroSchema.Parse(written);

        await Assert.That(schema.Aliases.Select(a => a.FullName)).IsEquivalentTo(new[] { "ns.Old", "other.Older" }, CollectionOrdering.Matching);
        await Assert.That(written).Contains("\"aliases\":[\"Old\",\"other.Older\"]");
        await Assert.That(reparsed.Aliases.Select(a => a.FullName)).IsEquivalentTo(new[] { "ns.Old", "other.Older" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SchemaName_ComparesByFullName()
    {
        await Assert.That(new SchemaName("R", "a.b")).IsEqualTo(new SchemaName("a.b.R"));
        await Assert.That(new SchemaName("R", "") == new SchemaName("R")).IsTrue();
        await Assert.That(new SchemaName("R", "a") != new SchemaName("R", "b")).IsTrue();
    }
}
