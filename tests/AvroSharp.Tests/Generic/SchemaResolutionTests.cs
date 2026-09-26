using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>The specification's schema resolution rules, through GenericDatumReader.Create(writer, reader).</summary>
public class SchemaResolutionTests
{
    [Test]
    [Arguments("\"int\"", "\"long\"")]
    [Arguments("\"int\"", "\"float\"")]
    [Arguments("\"int\"", "\"double\"")]
    [Arguments("\"long\"", "\"float\"")]
    [Arguments("\"long\"", "\"double\"")]
    [Arguments("\"float\"", "\"double\"")]
    public async Task Numbers_ArePromoted(string writerJson, string readerJson)
    {
        var (writer, reader) = (AvroSchema.Parse(writerJson), AvroSchema.Parse(readerJson));
        var value = writer.Type switch { AvroSchemaType.Int => (AvroValue)1234, AvroSchemaType.Long => (AvroValue)1234L, _ => (AvroValue)1234f };

        var read = Resolve(writer, reader, value);

        await Assert.That(read.Kind.ToString()).IsEqualTo(reader.Type.ToString());
        var number = reader.Type == AvroSchemaType.Long ? read.AsInt64() : read.AsDouble();
        await Assert.That(number).IsEqualTo(1234d);
    }

    [Test]
    public async Task StringsAndBytes_ConvertToEachOther()
    {
        var asBytes = Resolve(AvroSchema.String, AvroSchema.Bytes, "日本");
        var asString = Resolve(AvroSchema.Bytes, AvroSchema.String, new byte[] { 0x68, 0x69 });

        await Assert.That(asBytes.AsBytes()).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes("日本"));
        await Assert.That(asString.AsString()).IsEqualTo("hi");
    }

    [Test]
    [Arguments("\"long\"", "\"int\"")]
    [Arguments("\"double\"", "\"float\"")]
    [Arguments("\"string\"", "\"int\"")]
    [Arguments("{\"type\":\"fixed\",\"name\":\"F\",\"size\":2}", "{\"type\":\"fixed\",\"name\":\"F\",\"size\":3}")]
    [Arguments("{\"type\":\"enum\",\"name\":\"E\",\"symbols\":[\"A\"]}", "{\"type\":\"enum\",\"name\":\"Other\",\"symbols\":[\"A\"]}")]
    public async Task IncompatibleSchemas_AreRejectedWhenTheReaderIsCreated(string writerJson, string readerJson)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => GenericDatumReader.Create(AvroSchema.Parse(writerJson), AvroSchema.Parse(readerJson)));

        await Assert.That(ex.Message).Contains("cannot be read as");
    }

    private const string WriterRecord = """
        {"type":"record","name":"Person","namespace":"v1","fields":[
          {"name":"id","type":"int"},
          {"name":"nickname","type":"string"},
          {"name":"skipped_array","type":{"type":"array","items":{"type":"record","name":"Tag","fields":[{"name":"t","type":"string"}]}}},
          {"name":"skipped_map","type":{"type":"map","values":["null","double"]}},
          {"name":"skipped_misc","type":{"type":"record","name":"Misc","fields":[
            {"name":"b","type":"boolean"},{"name":"f","type":"float"},{"name":"by","type":"bytes"},
            {"name":"fx","type":{"type":"fixed","name":"Fx","size":3}},{"name":"e","type":{"type":"enum","name":"E","symbols":["X","Y"]}},
            {"name":"n","type":"null"}]}},
          {"name":"age","type":"int"}
        ]}
        """;

    private const string ReaderRecord = """
        {"type":"record","name":"Person","namespace":"v2","fields":[
          {"name":"age","type":"long"},
          {"name":"id","type":"long"},
          {"name":"name","type":"string","aliases":["nickname"]},
          {"name":"email","type":["null","string"],"default":null},
          {"name":"score","type":"double","default":1.5},
          {"name":"tags","type":{"type":"array","items":"string"},"default":["a","b"]},
          {"name":"home","type":{"type":"record","name":"Home","fields":[{"name":"city","type":"string"}]},"default":{"city":"Oslo"}}
        ]}
        """;

    [Test]
    public async Task Records_MatchFieldsByNameAndAlias_SkipWriterOnlyFields_AndFillDefaults()
    {
        var writer = (RecordSchema)AvroSchema.Parse(WriterRecord);
        var reader = (RecordSchema)AvroSchema.Parse(ReaderRecord);
        var misc = (RecordSchema)writer.GetField("skipped_misc").Schema;
        var tag = (RecordSchema)((ArraySchema)writer.GetField("skipped_array").Schema).Items;
        var value = new GenericRecord(writer)
        {
            ["id"] = 7,
            ["nickname"] = "Ada",
            ["skipped_array"] = AvroValue.FromArray(new AvroValue[] { new GenericRecord(tag) { ["t"] = "x" }, new GenericRecord(tag) { ["t"] = "y" } }),
            ["skipped_map"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["k"] = 2.5, ["n"] = AvroValue.Null }),
            ["skipped_misc"] = new GenericRecord(misc)
            {
                ["b"] = true, ["f"] = 1f, ["by"] = new byte[] { 1, 2 },
                ["fx"] = new GenericFixed((FixedSchema)misc.GetField("fx").Schema, [1, 2, 3]),
                ["e"] = AvroValue.FromEnum((EnumSchema)misc.GetField("e").Schema, "Y"),
                ["n"] = AvroValue.Null,
            },
            ["age"] = 36,
        };

        var read = Resolve(writer, reader, value).AsRecord();

        await Assert.That(read.Schema).IsSameReferenceAs(reader);
        await Assert.That(read["id"].AsInt64()).IsEqualTo(7L);
        await Assert.That(read["age"].AsInt64()).IsEqualTo(36L);        // after every skipped field
        await Assert.That(read["name"].AsString()).IsEqualTo("Ada");    // matched through the alias
        await Assert.That(read["email"].IsNull).IsTrue();
        await Assert.That(read["score"].AsDouble()).IsEqualTo(1.5);
        await Assert.That(read["tags"].AsArray().Select(t => t.AsString()).ToArray()).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(read["home"].AsRecord()["city"].AsString()).IsEqualTo("Oslo");
    }

    [Test]
    public async Task ReaderFieldWithoutDefault_ThatTheWriterLacks_IsRejectedWhenTheReaderIsCreated()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int"}]}""");

        var ex = Assert.Throws<AvroSchemaException>(() => GenericDatumReader.Create(writer, reader));

        await Assert.That(ex.Message).Contains("'R.b' is not in the writer's schema and has no default value");
    }

    [Test]
    public async Task MutableDefaults_AreNotSharedBetweenReads()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"home","type":{"type":"record","name":"Home","fields":[{"name":"city","type":"string"}]},"default":{"city":"Oslo"}}]}""");
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(new GenericRecord((RecordSchema)writer) { ["a"] = 1 });
        var resolver = GenericDatumReader.Create(writer, reader);

        // A record default is mutable; changing the one a read returned must not change the next read's.
        var first = resolver.Read(bytes).AsRecord();
        first["home"].AsRecord()["city"] = "Changed";
        var second = resolver.Read(bytes).AsRecord();

        await Assert.That(second["home"].AsRecord()["city"].AsString()).IsEqualTo("Oslo");
    }

    [Test]
    [Arguments("v2.Person", null)]              // same unqualified name
    [Arguments("Human", "v1.Person")]           // reader alias names the writer
    public async Task NamedTypes_MatchByUnqualifiedNameOrAlias(string readerName, string? alias)
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"Person","namespace":"v1","fields":[{"name":"a","type":"int"}]}""");
        var aliases = alias is null ? string.Empty : $$""","aliases":["{{alias}}"]""";
        var reader = AvroSchema.Parse($$"""{"type":"record","name":"{{readerName}}"{{aliases}},"fields":[{"name":"a","type":"int"}]}""");

        var read = Resolve(writer, reader, new GenericRecord((RecordSchema)writer) { ["a"] = 5 });

        await Assert.That(read.AsRecord()["a"].AsInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task EnumSymbols_AreMatchedByName_AndUnknownOnesTakeTheReaderDefault()
    {
        var writer = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["A","B","NEW"]}""");
        var reader = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["UNKNOWN","B","A"],"default":"UNKNOWN"}""");

        await Assert.That(Resolve(writer, reader, AvroValue.FromEnum(writer, "A")).AsEnumSymbol()).IsEqualTo("A");
        await Assert.That(Resolve(writer, reader, AvroValue.FromEnum(writer, "B")).AsEnumSymbol()).IsEqualTo("B");
        await Assert.That(Resolve(writer, reader, AvroValue.FromEnum(writer, "NEW")).AsEnumSymbol()).IsEqualTo("UNKNOWN");
    }

    [Test]
    public async Task UnknownEnumSymbolWithoutDefault_FailsOnlyWhenRead()
    {
        var writer = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["A","NEW"]}""");
        var reader = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["A"]}""");

        await Assert.That(Resolve(writer, reader, AvroValue.FromEnum(writer, "A")).AsEnumSymbol()).IsEqualTo("A");
        var ex = Assert.Throws<AvroDataException>(() => Resolve(writer, reader, AvroValue.FromEnum(writer, "NEW")));
        await Assert.That(ex.Message).Contains("'NEW' is not in the reader's enum 'E'");
    }

    [Test]
    public async Task Unions_ResolveEachWriterBranch_AndDeferMismatches()
    {
        var writer = AvroSchema.Parse("""["null","int","string"]""");
        var reader = AvroSchema.Parse("""["null","long"]""");

        await Assert.That(Resolve(writer, reader, AvroValue.Null).IsNull).IsTrue();
        await Assert.That(Resolve(writer, reader, 5).AsInt64()).IsEqualTo(5L);   // int promoted to the long branch
        var ex = Assert.Throws<AvroDataException>(() => Resolve(writer, reader, "text"));
        await Assert.That(ex.Message).Contains("cannot be read as");
    }

    [Test]
    public async Task NonUnionWriter_IsReadAsTheBestBranchOfAReaderUnion()
    {
        var reader = AvroSchema.Parse("""["null","string","double","long"]""");

        // No int branch: the first branch in union order that int promotes to (double, before long), as in Java.
        var promoted = Resolve(AvroSchema.Int, reader, 3);
        var exact = Resolve(AvroSchema.Long, reader, 4L);

        await Assert.That(promoted.Kind).IsEqualTo(AvroValueKind.Double);
        await Assert.That(exact.Kind).IsEqualTo(AvroValueKind.Long);
        await Assert.That(exact.AsInt64()).IsEqualTo(4L);
    }

    [Test]
    public async Task UnionWriter_IntoANonUnionReader_ReadsMatchingBranches()
    {
        var writer = AvroSchema.Parse("""["null","int"]""");

        await Assert.That(Resolve(writer, AvroSchema.Long, 9).AsInt64()).IsEqualTo(9L);
        Assert.Throws<AvroDataException>(() => Resolve(writer, AvroSchema.Long, AvroValue.Null));
    }

    [Test]
    public async Task ArraysAndMaps_ResolveTheirItems()
    {
        var array = Resolve(AvroSchema.Parse("""{"type":"array","items":"int"}"""), AvroSchema.Parse("""{"type":"array","items":"long"}"""), AvroValue.FromArray(new AvroValue[] { 1, 2, 3 }));
        var map = Resolve(AvroSchema.Parse("""{"type":"map","values":"float"}"""), AvroSchema.Parse("""{"type":"map","values":"double"}"""),
            AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["x"] = 0.5f }));

        await Assert.That(array.AsArray().Select(v => v.AsInt64()).ToArray()).IsEquivalentTo(new[] { 1L, 2L, 3L });
        await Assert.That(map.AsMap()["x"].AsDouble()).IsEqualTo(0.5);
    }

    [Test]
    public async Task RecursiveRecords_AreResolved()
    {
        var writer = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"int"},{"name":"extra","type":"string"},{"name":"next","type":["null","Node"]}]}""");
        var reader = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]},{"name":"v","type":"long"}]}""");
        var value = new GenericRecord(writer) { ["v"] = 1, ["extra"] = "x", ["next"] = new GenericRecord(writer) { ["v"] = 2, ["extra"] = "y", ["next"] = AvroValue.Null } };

        var read = Resolve(writer, reader, value).AsRecord();

        await Assert.That(read["v"].AsInt64()).IsEqualTo(1L);
        await Assert.That(read["next"].AsRecord()["v"].AsInt64()).IsEqualTo(2L);
        await Assert.That(read["next"].AsRecord()["next"].IsNull).IsTrue();
    }

    [Test]
    public async Task WriterOnlyArrays_WithRecordedBlockSizes_AreSkippedInOneStep()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"skip","type":{"type":"array","items":"long"}},{"name":"keep","type":"int"}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"keep","type":"int"}]}""");

        // skip: one block written with a negative count (-2) and its byte size (2), items 1 and 2, then the end; keep: 21.
        byte[] bytes = [0x03, 0x04, 0x02, 0x04, 0x00, 0x2A];
        var read = GenericDatumReader.Create(writer, reader).Read(bytes).AsRecord();

        await Assert.That(read["keep"].AsInt32()).IsEqualTo(21);
    }

    [Test]
    public async Task SkippedBlocksWithImpossibleCounts_AreRejected()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"skip","type":{"type":"array","items":"string"}},{"name":"keep","type":"int"}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"keep","type":"int"}]}""");

        // A block count of 1,000,000,000 strings in 3 bytes of input.
        byte[] bytes = [0x80, 0xA8, 0xD6, 0xB9, 0x07, 0x00, 0x2A];
        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, reader).Read(bytes));

        await Assert.That(ex.Message).Contains("larger than the remaining input");
    }

    [Test]
    public async Task ReadersForTheSameSchemaPair_AreCached()
    {
        var writer = AvroSchema.Parse("\"int\"");
        var reader = AvroSchema.Parse("\"long\"");

        await Assert.That(GenericDatumReader.Create(writer, reader)).IsSameReferenceAs(GenericDatumReader.Create(writer, reader));
        await Assert.That(GenericDatumReader.Create(writer, reader).ReaderSchema).IsSameReferenceAs(reader);
        await Assert.That(GenericDatumReader.Create(writer, writer).ReaderSchema).IsSameReferenceAs(writer);
    }

    private static AvroValue Resolve(AvroSchema writer, AvroSchema reader, AvroValue value) =>
        GenericDatumReader.Create(writer, reader).Read(GenericDatumWriter.Create(writer).WriteToArray(value));
}
