using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using TUnit.Assertions.Enums;

namespace AvroSharp.Tests.Generic;

public class GenericJsonTests
{
    private const string OrderSchema = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"id","type":"long"},
          {"name":"active","type":"boolean"},
          {"name":"count","type":"int"},
          {"name":"ratio","type":"float"},
          {"name":"total","type":"double"},
          {"name":"payload","type":"bytes"},
          {"name":"name","type":"string"},
          {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID"]}},
          {"name":"sku","type":{"type":"fixed","name":"Sku","size":2}},
          {"name":"tags","type":{"type":"array","items":"string"}},
          {"name":"attributes","type":{"type":"map","values":"int"}},
          {"name":"note","type":["null","string"]},
          {"name":"nothing","type":"null"}
        ]}
        """;

    [Test]
    public async Task EveryType_IsWrittenAsTheSpecificationDescribes()
    {
        var schema = (RecordSchema)AvroSchema.Parse(OrderSchema);
        var record = new GenericRecord(schema)
        {
            ["id"] = 42L,
            ["active"] = true,
            ["count"] = -7,
            ["ratio"] = 1.5f,
            ["total"] = 2.25,
            ["payload"] = new byte[] { 0x00, 0x41, 0xFF },
            ["name"] = "日本",
            ["status"] = AvroValue.FromEnum((EnumSchema)schema.GetField("status").Schema, "PAID"),
            ["sku"] = new GenericFixed((FixedSchema)schema.GetField("sku").Schema, [0x01, 0x7F]),
            ["tags"] = AvroValue.FromArray(new List<AvroValue> { "a", "b" }),
            ["attributes"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["k"] = 1 }),
            ["note"] = "hi",
            ["nothing"] = AvroValue.Null,
        };

        var json = GenericDatumJsonWriter.Create(schema).WriteToString(record);

        // Compare parsed JSON, not text, so escaping choices don't matter.
        var expected = """
            {"id":42,"active":true,"count":-7,"ratio":1.5,"total":2.25,"payload":"\u0000Aÿ","name":"日本",
             "status":"PAID","sku":"\u0001\u007F","tags":["a","b"],"attributes":{"k":1},"note":{"string":"hi"},"nothing":null}
            """;
        await Assert.That(Normalize(json)).IsEqualTo(Normalize(expected));
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(json)).IsEqualTo((AvroValue)record);
    }

    [Test]
    public async Task UnionBranches_AreNamedByFullName_AndNullIsNotWrapped()
    {
        var schema = AvroSchema.Parse("""
            ["null","int",{"type":"array","items":"int"},{"type":"map","values":"int"},
             {"type":"record","name":"Point","namespace":"geo","fields":[{"name":"x","type":"int"}]}]
            """);
        var point = new GenericRecord((RecordSchema)((UnionSchema)schema).Branches[4]) { ["x"] = 3 };
        var writer = GenericDatumJsonWriter.Create(schema);

        await Assert.That(writer.WriteToString(AvroValue.Null)).IsEqualTo("null");
        await Assert.That(writer.WriteToString(5)).IsEqualTo("""{"int":5}""");
        await Assert.That(writer.WriteToString(AvroValue.FromArray(new AvroValue[] { 1 }))).IsEqualTo("""{"array":[1]}""");
        await Assert.That(writer.WriteToString(AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal)))).IsEqualTo("""{"map":{}}""");
        await Assert.That(writer.WriteToString(point)).IsEqualTo("""{"geo.Point":{"x":3}}""");
    }

    [Test]
    public async Task UnionBranch_MayBeNamedWithoutNamespace_WhenUnambiguous()
    {
        var schema = AvroSchema.Parse("""["null",{"type":"record","name":"Point","namespace":"geo","fields":[{"name":"x","type":"int"}]}]""");
        var read = GenericDatumJsonReader.Create(schema).Read("""{"Point":{"x":3}}""");
        await Assert.That(read.AsRecord()["x"].AsInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task NonFiniteNumbers_AreStrings()
    {
        var schema = AvroSchema.Parse("""{"type":"array","items":"double"}""");
        var value = AvroValue.FromArray(new AvroValue[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1.0 });
        var json = GenericDatumJsonWriter.Create(schema).WriteToString(value);

        await Assert.That(json).IsEqualTo("""["NaN","Infinity","-Infinity",1]""");
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(json)).IsEqualTo(value);

        var floats = AvroSchema.Parse("""{"type":"array","items":"float"}""");
        var floatValue = AvroValue.FromArray(new AvroValue[] { float.NaN, float.NegativeInfinity, 0.1f });
        var floatJson = GenericDatumJsonWriter.Create(floats).WriteToString(floatValue);
        await Assert.That(GenericDatumJsonReader.Create(floats).Read(floatJson)).IsEqualTo(floatValue);
    }

    [Test]
    public async Task RecordFields_MayAppearInAnyOrder_AndMissingFieldsTakeTheirDefault()
    {
        var schema = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":"int"},
              {"name":"b","type":["null","string"],"default":null},
              {"name":"c","type":{"type":"array","items":"long"},"default":[1,2]},
              {"name":"d","type":["string","null"],"default":"x"}
            ]}
            """);
        var read = GenericDatumJsonReader.Create(schema).Read("""{"c":[5],"a":1}""").AsRecord();

        await Assert.That(read["a"].AsInt32()).IsEqualTo(1);
        await Assert.That(read["b"].IsNull).IsTrue();
        await Assert.That(read["c"].AsArray().Select(v => v.AsInt64()).ToArray()).IsEquivalentTo(new long[] { 5 }, CollectionOrdering.Matching);

        // A union default is not wrapped: it takes the first branch it matches.
        await Assert.That(read["d"].AsString()).IsEqualTo("x");
    }

    [Test]
    public async Task RecursiveRecord_RoundTrips()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"value","type":"int"},{"name":"next","type":["null","Node"]}]}""");
        var head = new GenericRecord(schema) { ["value"] = 1, ["next"] = new GenericRecord(schema) { ["value"] = 2, ["next"] = AvroValue.Null } };

        var json = GenericDatumJsonWriter.Create(schema).WriteToString(head);

        await Assert.That(json).IsEqualTo("""{"value":1,"next":{"Node":{"value":2,"next":null}}}""");
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(json)).IsEqualTo((AvroValue)head);
    }

    [Test]
    public async Task Utf8JsonReader_ReadsOneValueAtATime()
    {
        var schema = AvroSchema.Parse("""["null","int"]""");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("""[{"int":1},null,{"int":3}]"""));
        reader.Read();
        var values = new List<AvroValue>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(GenericDatumJsonReader.Create(schema).Read(ref reader));
        }

        await Assert.That(values).IsEquivalentTo(new AvroValue[] { 1, AvroValue.Null, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("""{"a":1,"b":2}""", "no field 'b'")]
    [Arguments("""{"a":1,"a":2}""", "more than once")]
    [Arguments("""{}""", "'a' of record 'R' is missing")]
    [Arguments("""{"a":1.5}""", "Expected an int, found the number 1.5")]
    [Arguments("""{"a":"1"}""", "Expected an int, found a string")]
    [Arguments("""{"a":1} x""", "Unexpected JSON after the value")]
    [Arguments("""{"a":""", "Invalid JSON")]
    public async Task InvalidInput_IsReportedAsAvroDataException(string json, string message)
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
        var utf8 = Encoding.UTF8.GetBytes(json);

        var fromBytes = Assert.Throws<AvroDataException>(() => GenericDatumJsonReader.Create(schema).Read(utf8));
        await Assert.That(fromBytes.Message).Contains(message);
    }

    [Test]
    [Arguments("""{"x":{"int":1,"long":2}}""", "exactly one property")]
    [Arguments("""{"x":{"float":1}}""", "'float' is not a branch")]
    [Arguments("""{"x":1}""", "Expected null or an object naming a union branch")]
    [Arguments("""{"x":{"bytes":"Ā"}}""", "above U+00FF")]
    [Arguments("""{"x":{"F":"abc"}}""", "length 2, found length 3")]
    [Arguments("""{"x":{"E":"C"}}""", "'C' is not a symbol")]
    public async Task InvalidUnionAndByteValues_AreRejected(string json, string message)
    {
        var schema = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[{"name":"x","type":["null","int","long","bytes",
              {"type":"fixed","name":"F","size":2},{"type":"enum","name":"E","symbols":["A","B"]}]}]}
            """);

        var ex = Assert.Throws<AvroDataException>(() => GenericDatumJsonReader.Create(schema).Read(json));
        await Assert.That(ex.Message).Contains(message);
    }

    [Test]
    public async Task ReadErrors_NameTheJsonPath()
    {
        var schema = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[{"name":"items","type":{"type":"array","items":
              {"type":"map","values":["null","int"]}}}]}
            """);

        var ex = Assert.Throws<AvroDataException>(() => GenericDatumJsonReader.Create(schema).Read("""{"items":[{},{"k":{"int":"x"}}]}"""));

        await Assert.That(ex.Message).StartsWith("At $.items[1]['k'].int: Expected an int");
    }

    [Test]
    public async Task WriteErrors_NameTheField()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","namespace":"n","fields":[{"name":"a","type":"int"}]}""");
        var record = new GenericRecord(schema) { ["a"] = "not an int" };

        var ex = Assert.Throws<AvroException>(() => GenericDatumJsonWriter.Create(schema).WriteToString(record));

        await Assert.That(ex.Message).StartsWith("Field 'n.R.a': A String value cannot be written as \"int\".");
    }

    /// <summary>The JSON writer widens numbers as the binary writer does: int to long, float or double; long to float or double; float to double.</summary>
    [Test]
    [Arguments("long", "int")]
    [Arguments("float", "int")]
    [Arguments("float", "long")]
    [Arguments("double", "int")]
    [Arguments("double", "long")]
    [Arguments("double", "float")]
    public async Task Writer_WidensNumbers(string type, string from)
    {
        var schema = AvroSchema.Parse($"\"{type}\"");
        // Each arm cast: a switch expression would give them their common type (float, or double).
        var value = from switch { "int" => (AvroValue)7, "long" => (AvroValue)7L, _ => (AvroValue)7f };
        var expected = type switch { "long" => (AvroValue)7L, "float" => (AvroValue)7f, _ => (AvroValue)7.0 };

        var json = GenericDatumJsonWriter.Create(schema).WriteToString(value);

        await Assert.That(GenericDatumJsonReader.Create(schema).Read(json)).IsEqualTo(expected);
        await Assert.That(GenericDatumWriter.Create(schema).WriteToArray(value)).IsEquivalentTo(GenericDatumWriter.Create(schema).WriteToArray(expected), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("union", "A String value cannot be written as [\"null\",\"int\"].")]
    [Arguments("record", "A Record value cannot be written as {\"name\":\"R\",\"type\":\"record\",\"fields\":[]}.")]
    [Arguments("enum", "A Enum value cannot be written as {\"name\":\"E\",\"type\":\"enum\",\"symbols\":[\"A\"]}.")]
    [Arguments("fixed", "A Fixed value cannot be written as {\"name\":\"F\",\"type\":\"fixed\",\"size\":2}.")]
    [Arguments("array", "A Int value cannot be written as an array.")]
    [Arguments("map", "A Null value cannot be written as a map.")]
    [Arguments("null", "A Int value cannot be written as \"null\".")]
    [Arguments("narrowing", "A Long value cannot be written as \"int\".")]
    public async Task Writer_RejectsValuesOfTheWrongShape(string what, string message)
    {
        var otherRecord = new GenericRecord((RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Other","fields":[]}"""));
        var (schema, value) = what switch
        {
            "union" => (AvroSchema.Parse("""["null","int"]"""), (AvroValue)"text"),
            "record" => (AvroSchema.Parse("""{"type":"record","name":"R","fields":[]}"""), (AvroValue)otherRecord),
            "enum" => (AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["A"]}"""), AvroValue.FromEnum(new EnumSchema(new SchemaName("Other"), ["A"]), 0)),
            "fixed" => (AvroSchema.Parse("""{"type":"fixed","name":"F","size":2}"""), (AvroValue)new GenericFixed(new FixedSchema(new SchemaName("G"), 2), new byte[2])),
            "array" => (AvroSchema.Parse("""{"type":"array","items":"int"}"""), (AvroValue)5),
            "map" => (AvroSchema.Parse("""{"type":"map","values":"int"}"""), AvroValue.Null),
            "null" => (AvroSchema.Parse("\"null\""), (AvroValue)5),
            _ => (AvroSchema.Parse("\"int\""), (AvroValue)5L),
        };

        // The binary writer rejects the same values with the same messages.
        var json = Assert.Throws<AvroException>(() => GenericDatumJsonWriter.Create(schema).WriteToString(value));
        var binary = Assert.Throws<AvroException>(() => GenericDatumWriter.Create(schema).WriteToArray(value));

        await Assert.That(json.Message).IsEqualTo(message);
        await Assert.That(binary.Message).IsEqualTo(message);
    }

    [Test]
    public async Task WriterMaxDepth_IsConfigurable()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"List","fields":[{"name":"next","type":["null","List"]}]}""");
        var head = new GenericRecord(schema);
        var current = head;
        for (var i = 1; i < 10; i++)
        {
            var next = new GenericRecord(schema);
            current["next"] = next;
            current = next;
        }

        // A chain of 10 records: 10 levels are allowed with MaxDepth = 10, and rejected with 9.
        var ex = Assert.Throws<AvroException>(() => GenericDatumJsonWriter.Create(schema, new GenericDatumWriterOptions { MaxDepth = 9 }).WriteToString(head));
        var json = GenericDatumJsonWriter.Create(schema, new GenericDatumWriterOptions { MaxDepth = 10 }).WriteToString(head);

        await Assert.That(ex.Message).Contains("Records are nested more than 9 levels deep (GenericDatumWriterOptions.MaxDepth); a record may contain itself.");
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(json)).IsEqualTo((AvroValue)head);
    }

    [Test]
    public async Task ARecordContainingItself_IsRejectedByTheWriter()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Loop","fields":[{"name":"next","type":["null","Loop"]}]}""");
        var record = new GenericRecord(schema);
        record["next"] = record;

        var ex = Assert.Throws<AvroException>(() => GenericDatumJsonWriter.Create(schema).WriteToString(record));

        await Assert.That(ex.Message).Contains("nested more than 128 levels");
    }

    /// <summary>A map may be any <see cref="IReadOnlyDictionary{TKey, TValue}"/>, not only a <see cref="Dictionary{TKey, TValue}"/>.</summary>
    [Test]
    [Arguments("sorted")]
    [Arguments("read-only")]
    public async Task MapsOfAnyReadOnlyDictionary_AreWritten(string kind)
    {
        var schema = AvroSchema.Parse("""{"type":"map","values":"int"}""");
        var entries = new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["a"] = 1, ["b"] = 2 };
        IReadOnlyDictionary<string, AvroValue> other = string.Equals(kind, "sorted", StringComparison.Ordinal)
            ? new SortedDictionary<string, AvroValue>(entries, StringComparer.Ordinal)
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, AvroValue>(entries);

        var binary = GenericDatumWriter.Create(schema).WriteToArray(AvroValue.FromMap(other));
        var json = GenericDatumJsonWriter.Create(schema).WriteToString(AvroValue.FromMap(other));

        await Assert.That(binary).IsEquivalentTo(GenericDatumWriter.Create(schema).WriteToArray(AvroValue.FromMap(entries)), CollectionOrdering.Matching);
        await Assert.That(Normalize(json)).IsEqualTo(Normalize("""{"a":1,"b":2}"""));
    }

    [Test]
    public async Task DeeplyNestedRecords_AreRejected_NotAStackOverflow()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]}]}""");
        var json = new StringBuilder();
        const int Depth = 200;
        for (var i = 0; i < Depth; i++)
        {
            json.Append("""{"next":{"Node":""");
        }

        json.Append("""{"next":null}""").Append('}', 2 * Depth);

        var reader = GenericDatumJsonReader.Create(schema, new GenericDatumReaderOptions { MaxDepth = 100 });
        var ex = Assert.Throws<AvroDataException>(() => reader.Read(json.ToString()));

        await Assert.That(ex.Message).Contains("nested more than 100 levels");
    }

    [Test]
    public async Task Indented_ProducesTheSameValue()
    {
        var schema = AvroSchema.Parse("""{"type":"map","values":"int"}""");
        var value = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["a"] = 1, ["b"] = 2 });
        var writer = GenericDatumJsonWriter.Create(schema);

        var indented = writer.WriteToUtf8Bytes(value, indented: true);

        await Assert.That(Encoding.UTF8.GetString(indented)).Contains("\n");
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(indented)).IsEqualTo(value);
    }

    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
