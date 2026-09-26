using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

public class GenericDatumTests
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
          {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID","SHIPPED"]}},
          {"name":"sku","type":{"type":"fixed","name":"Sku","size":4}},
          {"name":"tags","type":{"type":"array","items":"string"}},
          {"name":"counts","type":{"type":"array","items":"long"}},
          {"name":"small","type":{"type":"array","items":"int"}},
          {"name":"weights","type":{"type":"array","items":"double"}},
          {"name":"ratios","type":{"type":"array","items":"float"}},
          {"name":"attributes","type":{"type":"map","values":"double"}},
          {"name":"note","type":["null","string"]},
          {"name":"line","type":{"type":"record","name":"Line","fields":[{"name":"qty","type":"int"}]}},
          {"name":"nothing","type":"null"}
        ]}
        """;

    [Test]
    public async Task EveryType_RoundTrips()
    {
        var schema = (RecordSchema)AvroSchema.Parse(OrderSchema);
        var record = CreateOrder(schema);

        var bytes = GenericDatumWriter.Create(schema).WriteToArray(record);
        var read = GenericDatumReader.Create(schema).Read(bytes);

        await Assert.That(read.Kind).IsEqualTo(AvroValueKind.Record);
        await Assert.That(read).IsEqualTo((AvroValue)record);
        await Assert.That(read.AsRecord()["status"].AsEnumSymbol()).IsEqualTo("PAID");
        await Assert.That(read.AsRecord()["counts"].AsArray().Count).IsEqualTo(40);
    }

    [Test]
    public async Task SpecificationRecordExample_IsEncodedExactly()
    {
        // {"a": 27, "b": "foo"} encodes as 36 06 66 6f 6f.
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"test","fields":[{"name":"a","type":"long"},{"name":"b","type":"string"}]}""");
        var record = new GenericRecord(schema) { ["a"] = 27L, ["b"] = "foo" };
        await Assert.That(Convert.ToHexString(GenericDatumWriter.Create(schema).WriteToArray(record))).IsEqualTo("3606666F6F");
    }

    [Test]
    public async Task RecursiveRecord_RoundTrips()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"value","type":"int"},{"name":"next","type":["null","Node"]}]}""");
        var head = new GenericRecord(schema) { ["value"] = 1 };
        var tail = new GenericRecord(schema) { ["value"] = 2, ["next"] = AvroValue.Null };
        head["next"] = tail;

        var bytes = GenericDatumWriter.Create(schema).WriteToArray(head);
        var read = GenericDatumReader.Create(schema).Read(bytes).AsRecord();

        await Assert.That(read["value"].AsInt32()).IsEqualTo(1);
        await Assert.That(read["next"].AsRecord()["value"].AsInt32()).IsEqualTo(2);
        await Assert.That(read["next"].AsRecord()["next"].IsNull).IsTrue();
    }

    [Test]
    public async Task Union_SelectsTheBranchFromTheValue()
    {
        var schema = AvroSchema.Parse("""
            ["null","int","string",{"type":"record","name":"A","fields":[]},{"type":"record","name":"B","fields":[]},{"type":"enum","name":"E","symbols":["X"]}]
            """);
        var union = (UnionSchema)schema;
        var writer = GenericDatumWriter.Create(schema);

        await Assert.That(writer.WriteToArray(AvroValue.Null)[0]).IsEqualTo((byte)0);
        await Assert.That(writer.WriteToArray(5)[0]).IsEqualTo((byte)2);
        await Assert.That(writer.WriteToArray("s")[0]).IsEqualTo((byte)4);
        await Assert.That(writer.WriteToArray(new GenericRecord((RecordSchema)union.Branches[3]))[0]).IsEqualTo((byte)6);
        await Assert.That(writer.WriteToArray(new GenericRecord((RecordSchema)union.Branches[4]))[0]).IsEqualTo((byte)8);
        await Assert.That(writer.WriteToArray(AvroValue.FromEnum((EnumSchema)union.Branches[5], "X"))[0]).IsEqualTo((byte)10);
    }

    [Test]
    public async Task Union_MatchesNamedBranchesByNameWhenTheSchemaInstanceDiffers()
    {
        // The value's record schema is a separate instance with the same full name as the union branch.
        var union = AvroSchema.Parse("""["null",{"type":"record","name":"A","fields":[{"name":"x","type":"int"}]}]""");
        var separate = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"A","fields":[{"name":"x","type":"int"}]}""");
        var value = new GenericRecord(separate) { ["x"] = 7 };

        var bytes = GenericDatumWriter.Create(union).WriteToArray(value);
        await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("020E");
    }

    [Test]
    public async Task Union_WidensNumbersWhenTheExactKindIsMissing()
    {
        var schema = AvroSchema.Parse("""["null","long","double"]""");
        var writer = GenericDatumWriter.Create(schema);
        var reader = GenericDatumReader.Create(schema);

        // An int goes to the long branch, a float to the double branch.
        await Assert.That(reader.Read(writer.WriteToArray(7))).IsEqualTo((AvroValue)7L);
        await Assert.That(reader.Read(writer.WriteToArray(1.5f))).IsEqualTo((AvroValue)1.5);
    }

    [Test]
    public async Task Writer_ReportsMismatchesWithTheFieldPath()
    {
        var schema = (RecordSchema)AvroSchema.Parse(OrderSchema);
        var record = CreateOrder(schema);
        record["count"] = "not an int";

        var ex = Assert.Throws<AvroException>(() => GenericDatumWriter.Create(schema).WriteToArray(record));
        await Assert.That(ex.Message).Contains("Field 'shop.Order.count'");
        await Assert.That(ex.Message).Contains("String value cannot be written as \"int\"");
    }

    [Test]
    [Arguments("union")]
    [Arguments("record")]
    [Arguments("enum")]
    [Arguments("fixed")]
    [Arguments("array")]
    public async Task Writer_RejectsValuesOfTheWrongShape(string what)
    {
        var otherRecord = new GenericRecord((RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Other","fields":[]}"""));
        var (schema, value) = what switch
        {
            "union" => (AvroSchema.Parse("""["null","int"]"""), (AvroValue)"text"),
            "record" => (AvroSchema.Parse("""{"type":"record","name":"R","fields":[]}"""), (AvroValue)otherRecord),
            "enum" => (AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["A"]}"""), AvroValue.FromEnum(new EnumSchema(new SchemaName("F"), ["A"]), 0)),
            "fixed" => (AvroSchema.Parse("""{"type":"fixed","name":"F","size":2}"""), (AvroValue)new GenericFixed(new FixedSchema(new SchemaName("G"), 2), new byte[2])),
            _ => (AvroSchema.Parse("""{"type":"array","items":"int"}"""), (AvroValue)5),
        };

        var ex = Assert.Throws<AvroException>(() => GenericDatumWriter.Create(schema).WriteToArray(value));
        await Assert.That(ex.Message).Contains("cannot be written");
    }

    [Test]
    [Arguments("""{"type":"enum","name":"E","symbols":["A","B"]}""", "04", "ordinal 2 is out of range")]
    [Arguments("""["null","int"]""", "04", "branch index 2 is out of range")]
    [Arguments("""{"type":"array","items":"long"}""", "C8010000", "larger than the remaining input")]
    [Arguments("""{"type":"map","values":"null"}""", "0400", "larger than the remaining input")]
    [Arguments("""{"type":"array","items":"null"}""", "FEFFFFFFFF0F", "MaxZeroSizeItems")]
    public async Task Reader_RejectsMalformedData(string schemaJson, string hex, string reason)
    {
        var reader = GenericDatumReader.Create(AvroSchema.Parse(schemaJson));
        var ex = Assert.Throws<AvroDataException>(() => reader.Read(Convert.FromHexString(hex)));
        await Assert.That(ex.Message).Contains(reason);
    }

    [Test]
    public async Task ArrayOfNulls_WithinTheCap_IsRead()
    {
        // Zero-size items are allowed up to the per-block cap; 1,000 nulls take only the count's two bytes.
        var reader = GenericDatumReader.Create(AvroSchema.Parse("""{"type":"array","items":"null"}"""));
        var value = reader.Read(Convert.FromHexString("D00F00"));
        await Assert.That(value.AsArray().Count).IsEqualTo(1000);
    }

    [Test]
    public async Task ReadersAndWriters_AreCachedPerSchema()
    {
        var schema = AvroSchema.Parse("""{"type":"array","items":"int"}""");
        await Assert.That(GenericDatumWriter.Create(schema)).IsSameReferenceAs(GenericDatumWriter.Create(schema));
        await Assert.That(GenericDatumReader.Create(schema)).IsSameReferenceAs(GenericDatumReader.Create(schema));
    }

    [Test]
    public async Task Write_ToAnAvroWriter_AppendsToOtherData()
    {
        var schema = AvroSchema.Parse("\"string\"");
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        writer.WriteInt(1);
        GenericDatumWriter.Create(schema).Write(ref writer, "x");
        writer.Flush();

        await Assert.That(Convert.ToHexString(output.WrittenSpan.ToArray())).IsEqualTo("020278");
    }

    internal static GenericRecord CreateOrder(RecordSchema schema)
    {
        var status = (EnumSchema)schema.GetField("status").Schema;
        var sku = (FixedSchema)schema.GetField("sku").Schema;
        var line = (RecordSchema)schema.GetField("line").Schema;
        return new GenericRecord(schema)
        {
            ["id"] = long.MaxValue,
            ["active"] = true,
            ["count"] = -64,
            ["ratio"] = 0.25f,
            ["total"] = 1234.5,
            ["payload"] = new byte[] { 1, 2, 3 },
            ["name"] = "Grüße",
            ["status"] = AvroValue.FromEnum(status, "PAID"),
            ["sku"] = new GenericFixed(sku, [9, 8, 7, 6]),
            ["tags"] = AvroValue.FromArray(new List<AvroValue> { "a", "b" }),
            ["counts"] = AvroValue.FromArray(Enumerable.Range(0, 40).Select(i => (AvroValue)(long)(i * 1000 - 20000)).ToList()),
            ["small"] = AvroValue.FromArray(Enumerable.Range(-30, 60).Select(i => (AvroValue)i).ToArray()),
            ["weights"] = AvroValue.FromArray(Enumerable.Range(0, 25).Select(i => (AvroValue)(i * 0.5 - 3)).ToList()),
            ["ratios"] = AvroValue.FromArray(Enumerable.Range(0, 25).Select(i => (AvroValue)(i * 0.25f)).ToList()),
            ["attributes"] = AvroValue.FromMap(new Dictionary<string, AvroValue> { ["w"] = 1.5, ["h"] = -2.0 }),
            ["note"] = "hello",
            ["line"] = new GenericRecord(line) { ["qty"] = 3 },
            ["nothing"] = AvroValue.Null,
        };
    }
}
