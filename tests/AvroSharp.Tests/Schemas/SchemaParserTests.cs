using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

public class SchemaParserTests
{
    [Test]
    [Arguments("\"int\"")]
    [Arguments("""{"type":"int"}""")]
    public async Task Primitive_ReturnsTheSharedInstance(string json)
    {
        await Assert.That(AvroSchema.Parse(json)).IsSameReferenceAs(AvroSchema.Int);
    }

    [Test]
    public async Task Record_ReadsAllFieldAttributes()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","doc":"A record","fields":[
              {"name":"a","type":"int","doc":"first","default":1,"order":"descending","aliases":["x","y"],"custom":true},
              {"name":"b","type":"string","order":"ignore"},
              {"name":"c","type":["null","long"],"default":null}
            ]}
            """);

        await Assert.That(schema.Doc).IsEqualTo("A record");
        await Assert.That(schema.Fields.Count).IsEqualTo(3);

        var a = schema.Fields[0];
        await Assert.That(a.Position).IsEqualTo(0);
        await Assert.That(a.Record).IsSameReferenceAs(schema);
        await Assert.That(a.Doc).IsEqualTo("first");
        await Assert.That(a.DefaultValue!.Value.GetInt32()).IsEqualTo(1);
        await Assert.That(a.Order).IsEqualTo(FieldOrder.Descending);
        await Assert.That(a.Aliases).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(a.Properties["custom"].GetBoolean()).IsTrue();

        await Assert.That(schema.Fields[1].Order).IsEqualTo(FieldOrder.Ignore);
        await Assert.That(schema.Fields[1].HasDefaultValue).IsFalse();

        // A JSON null default is a default (for a union whose first matching branch is null).
        await Assert.That(schema.Fields[2].HasDefaultValue).IsTrue();
        await Assert.That(schema.Fields[2].Position).IsEqualTo(2);
    }

    [Test]
    public async Task Record_FieldLookup_WorksBelowAndAboveTheIndexThreshold()
    {
        var fields = string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"name":"f{{i}}","type":"int"}"""));
        var large = (RecordSchema)AvroSchema.Parse($$"""{"type":"record","name":"L","fields":[{{fields}}]}""");
        var small = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"S","fields":[{"name":"a","type":"int"}]}""");

        await Assert.That(large.GetField("f17").Position).IsEqualTo(17);
        await Assert.That(large.TryGetField("nope", out _)).IsFalse();
        await Assert.That(small.GetField("a").Position).IsEqualTo(0);
        await Assert.That(small.TryGetField("A", out _)).IsFalse();
    }

    [Test]
    public async Task Error_IsARecordFlaggedAsError()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"error","name":"Oops","fields":[]}""");
        await Assert.That(schema.IsError).IsTrue();
        await Assert.That(schema.CanonicalForm).IsEqualTo("""{"name":"Oops","type":"record","fields":[]}""");
    }

    [Test]
    [Arguments("""{"type":"record","name":"R"}""", "$", "must have a 'fields' array")]
    [Arguments("""{"type":"record","name":"R","fields":{}}""", "$.fields", "must have a 'fields' array")]
    [Arguments("""{"type":"record","name":"R","fields":[{"type":"int"}]}""", "$.fields[0]", "must have a 'name'")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a"}]}""", "$.fields[0]", "must have a 'type'")]
    [Arguments("""{"type":"record","name":"R","fields":["int"]}""", "$.fields[0]", "must be a JSON object")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"a","type":"long"}]}""", "$.fields", "more than one field named 'a'")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a","type":"int","order":"up"}]}""", "$.fields[0].order", "'order' attribute")]
    [Arguments("""{"type":"record","fields":[]}""", "$", "must have a 'name'")]
    [Arguments("""{"name":"R","fields":[]}""", "$", "must have a 'type' attribute")]
    [Arguments("""{"type":7}""", "$.type", "must be a string")]
    [Arguments("""{"type":"record","name":"R","doc":5,"fields":[]}""", "$.doc", "'doc' attribute must be a string")]
    [Arguments("""{"type":"enum","name":"E"}""", "$", "must have a 'symbols' array")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A","A"]}""", "$", "more than once")]
    [Arguments("""{"type":"enum","name":"E","symbols":["1"]}""", "$", "not a valid symbol")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A",2]}""", "$.symbols[1]", "array of strings")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A"],"default":"B"}""", "$", "is not one of its symbols")]
    [Arguments("""{"type":"fixed","name":"F"}""", "$", "must have a 'size'")]
    [Arguments("""{"type":"fixed","name":"F","size":-1}""", "$.size", "non-negative integer")]
    [Arguments("""{"type":"fixed","name":"F","size":1.5}""", "$.size", "non-negative integer")]
    [Arguments("""{"type":"fixed","name":"F","size":"4"}""", "$.size", "non-negative integer")]
    [Arguments("""{"type":"array"}""", "$", "must have an 'items' attribute")]
    [Arguments("""{"type":"map"}""", "$", "must have a 'values' attribute")]
    [Arguments("""["int","int"]""", "$", "more than one 'int' branch")]
    [Arguments("""["long",{"type":"long","logicalType":"timestamp-millis"}]""", "$", "more than one 'long' branch")]
    [Arguments("""["null",["int"]]""", "$", "may not immediately contain other unions")]
    [Arguments("""[{"type":"array","items":"int"},{"type":"array","items":"long"}]""", "$", "more than one 'array' branch")]
    [Arguments("12", "$", "must be a JSON string")]
    [Arguments("\"record\"", "$", "is not a defined type")]
    public async Task InvalidSchema_IsRejectedWithPath(string json, string path, string reason)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json));
        await Assert.That(ex.Path).IsEqualTo(path);
        await Assert.That(ex.Reason!).Contains(reason);
    }

    [Test]
    public async Task Union_OfDifferentlyNamedTypes_IsAllowed()
    {
        var schema = (UnionSchema)AvroSchema.Parse("""
            [{"type":"fixed","name":"A","size":1},{"type":"fixed","name":"B","size":1},"null"]
            """);
        await Assert.That(schema.Branches.Count).IsEqualTo(3);
    }

    [Test]
    public async Task EmptyUnion_IsAllowed()
    {
        var schema = (UnionSchema)AvroSchema.Parse("[]");
        await Assert.That(schema.Branches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Enum_ReadsSymbolsAndDefault()
    {
        var schema = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"Suit","symbols":["SPADES","HEARTS"],"default":"HEARTS"}""");
        await Assert.That(schema.Symbols).IsEquivalentTo(new[] { "SPADES", "HEARTS" });
        await Assert.That(schema.Default).IsEqualTo("HEARTS");
        await Assert.That(schema.TryGetOrdinal("HEARTS", out var ordinal)).IsTrue();
        await Assert.That(ordinal).IsEqualTo(1);
        await Assert.That(schema.TryGetOrdinal("CLUBS", out _)).IsFalse();
    }

    [Test]
    public async Task ArrayAndMap_KeepCustomProperties()
    {
        var array = (ArraySchema)AvroSchema.Parse("""{"type":"array","items":"int","java-class":"x"}""");
        var map = (MapSchema)AvroSchema.Parse("""{"type":"map","values":"bytes","meta":{"a":1}}""");
        await Assert.That(array.Items).IsSameReferenceAs(AvroSchema.Int);
        await Assert.That(array.Properties["java-class"].GetString()).IsEqualTo("x");
        await Assert.That(map.Values).IsSameReferenceAs(AvroSchema.Bytes);
        await Assert.That(map.Properties["meta"].GetProperty("a").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedJson_ReportsLineAndColumn()
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse("{\n  \"type\": \"record\",\n  \"name\": }"));
        await Assert.That(ex.Reason!).Contains("not valid JSON");
        await Assert.That(ex.LineNumber).IsEqualTo(3);
    }

    [Test]
    public async Task SchemaError_ReportsLineAndColumnOfTheOffendingValue()
    {
        const string json = "{\n  \"type\": \"record\",\n  \"name\": \"R\",\n  \"fields\": [\n    {\"name\": \"a\", \"type\": \"Nope\"}\n  ]\n}";
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json));
        await Assert.That(ex.Path).IsEqualTo("$.fields[0].type");
        await Assert.That(ex.LineNumber).IsEqualTo(5);
        await Assert.That(ex.BytePositionInLine).IsEqualTo(27);
        await Assert.That(ex.Message).Contains("(at $.fields[0].type, line 5, column 27)");
    }

    [Test]
    public async Task TrailingContent_IsRejected()
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse("\"int\" \"long\""));
        await Assert.That(ex.Message).IsNotNull();
    }

    [Test]
    public async Task Comments_AreRejectedByDefaultAndAcceptedWhenEnabled()
    {
        const string json = """
            {
              // a comment
              "type": "array", "items": "int", /* trailing comma follows */
            }
            """;
        Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json));
        var schema = AvroSchema.Parse(json, new AvroSchemaParseOptions { AllowComments = true });
        await Assert.That(schema.CanonicalForm).IsEqualTo("""{"type":"array","items":"int"}""");
    }

    [Test]
    public async Task MaxDepth_IsEnforced()
    {
        var json = string.Concat(Enumerable.Repeat("""{"type":"array","items":""", 10)) + "\"int\"" + new string('}', 10);
        Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json, new AvroSchemaParseOptions { MaxDepth = 5 }));
        await Assert.That(AvroSchema.Parse(json).Type).IsEqualTo(AvroSchemaType.Array);
    }

    [Test]
    public async Task ParseUtf8_AndParseAsync_MatchParseString()
    {
        var json = LargeSchemaJson();
        var fromString = AvroSchema.Parse(json);
        var fromUtf8 = AvroSchema.Parse(Encoding.UTF8.GetBytes(json));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var fromStream = await AvroSchema.ParseAsync(stream);

        await Assert.That(fromUtf8.CanonicalForm).IsEqualTo(fromString.CanonicalForm);
        await Assert.That(fromStream.CanonicalForm).IsEqualTo(fromString.CanonicalForm);
        await Assert.That(fromStream.ToJson()).IsEqualTo(fromString.ToJson());
    }

    [Test]
    public async Task NonAsciiText_IsPreserved()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","doc":"Grüße 日本 é","fields":[]}""");
        await Assert.That(schema.Doc).IsEqualTo("Grüße 日本 é");
        await Assert.That(AvroSchema.Parse(schema.ToJson()) is RecordSchema { Doc: "Grüße 日本 é" }).IsTrue();
    }

    [Test]
    public async Task Parser_RemembersNamedTypesAcrossCalls()
    {
        var parser = new AvroSchemaParser();
        parser.Parse("""{"type":"fixed","name":"com.acme.Id","size":16}""");
        var record = (RecordSchema)parser.Parse("""{"type":"record","name":"User","namespace":"com.acme","fields":[{"name":"id","type":"Id"}]}""");

        await Assert.That(record.Fields[0].Schema).IsSameReferenceAs(parser.NamedSchemas["com.acme.Id"]);
        await Assert.That(parser.NamedSchemas.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parser_DoesNotKeepNamesFromAFailedParse()
    {
        var parser = new AvroSchemaParser();
        Assert.Throws<AvroSchemaException>(() => parser.Parse("""
            {"type":"record","name":"R","fields":[{"name":"a","type":{"type":"fixed","name":"F","size":1}},{"name":"b","type":"Missing"}]}
            """));
        await Assert.That(parser.NamedSchemas.Count).IsEqualTo(0);

        // The same names can be defined by a later, valid schema.
        parser.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":{"type":"fixed","name":"F","size":1}}]}""");
        await Assert.That(parser.NamedSchemas.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parser_AddNamedSchemas_RegistersNestedTypes()
    {
        var inner = new FixedSchema(new SchemaName("ns.F"), 4);
        var outer = new RecordSchema(new SchemaName("ns.R"), [new RecordField("f", inner)]);
        var parser = new AvroSchemaParser();
        parser.AddNamedSchemas(outer);

        var schema = (ArraySchema)parser.Parse("""{"type":"array","items":"ns.F"}""");
        await Assert.That(schema.Items).IsSameReferenceAs(inner);

        Assert.Throws<AvroSchemaException>(() => parser.AddNamedSchemas(new FixedSchema(new SchemaName("ns.F"), 8)));
    }

    internal static string LargeSchemaJson()
    {
        // Larger than the parser's stack buffer, so the pooled-buffer path is used.
        var fields = string.Join(",", Enumerable.Range(0, 60).Select(i => $$"""{"name":"field_{{i}}","type":["null","string"],"default":null,"doc":"Field number {{i}}"}"""));
        return $$"""{"type":"record","name":"Large","namespace":"bench","fields":[{{fields}}]}""";
    }
}
