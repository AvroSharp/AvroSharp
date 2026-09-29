using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using TUnit.Assertions.Enums;

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
        var asString = Resolve(AvroSchema.Bytes, AvroSchema.String, "hi"u8.ToArray());

        await Assert.That(asBytes.AsBytes()).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes("日本"), CollectionOrdering.Matching);
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
                ["b"] = true,
                ["f"] = 1f,
                ["by"] = new byte[] { 1, 2 },
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
        await Assert.That(read["tags"].AsArray().Select(t => t.AsString()).ToArray()).IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
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

    /// <summary>Enums and fixed types match by a reader alias as records do: on their own, and as a union branch.</summary>
    [Test]
    [Arguments("enum")]
    [Arguments("fixed")]
    public async Task EnumsAndFixedTypes_MatchByAlias(string kind)
    {
        var isEnum = string.Equals(kind, "enum", StringComparison.Ordinal);
        var (writerJson, readerJson, value) = isEnum
            ? ("""{"type":"enum","name":"Color","namespace":"v1","symbols":["RED","BLUE"]}""",
               """{"type":"enum","name":"Hue","namespace":"v2","aliases":["v1.Color"],"symbols":["BLUE","RED"]}""",
               (Func<AvroSchema, AvroValue>)(s => AvroValue.FromEnum((EnumSchema)s, "BLUE")))
            : ("""{"type":"fixed","name":"Hash","namespace":"v1","size":2}""",
               """{"type":"fixed","name":"Digest","namespace":"v2","aliases":["v1.Hash"],"size":2}""",
               s => (AvroValue)new GenericFixed((FixedSchema)s, [0xAB, 0xCD]));
        var writer = AvroSchema.Parse(writerJson);
        var reader = AvroSchema.Parse(readerJson);
        var inUnion = AvroSchema.Parse($"""["null",{readerJson}]""");

        var read = Resolve(writer, reader, value(writer));
        var readAsBranch = Resolve(writer, inUnion, value(writer));

        var expected = isEnum ? "BLUE" : "ABCD";
        await Assert.That(isEnum ? read.AsEnumSymbol() : Convert.ToHexString(read.AsFixed().GetBytesUnsafe())).IsEqualTo(expected);
        await Assert.That(isEnum ? readAsBranch.AsEnumSymbol() : Convert.ToHexString(readAsBranch.AsFixed().GetBytesUnsafe())).IsEqualTo(expected);
    }

    [Test]
    public async Task EnumsAndFixedTypes_WithoutAMatchingNameOrAlias_AreIncompatible()
    {
        var writer = AvroSchema.Parse("""{"type":"fixed","name":"Hash","namespace":"v1","size":2}""");
        var reader = AvroSchema.Parse("""{"type":"fixed","name":"Digest","namespace":"v2","aliases":["v1.Other"],"size":2}""");

        var ex = Assert.Throws<AvroSchemaException>(() => GenericDatumReader.Create(writer, reader));

        await Assert.That(ex.Message).Contains("the names differ");
    }

    /// <summary>The resolving reader applies the options it is given (#129), not only the default limits.</summary>
    [Test]
    public async Task TheResolvingReader_UsesItsOptions()
    {
        // 90,000 empty records exceed the default zero-size budget of 65,536; a reader with a higher one reads them.
        const string Empties = """{"type":"array","items":{"type":"record","name":"Empty","fields":[]}}""";
        var writer = AvroSchema.Parse(Empties);
        var reader = AvroSchema.Parse(Empties);
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var w = new AvroWriter(output);
        w.WriteBlockCount(90_000);
        w.WriteBlockEnd();
        w.Flush();
        var bytes = output.WrittenSpan.ToArray();

        var limited = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, reader).Read(bytes));
        var generous = GenericDatumReader.Create(writer, reader, new GenericDatumReaderOptions { MaxZeroSizeItems = 100_000 }).Read(bytes);

        // And MaxDepth: a chain of 10 records is rejected with a limit of 5.
        var list = AvroSchema.Parse("""{"type":"record","name":"List","fields":[{"name":"next","type":["null","List"]}]}""");
        var listReader = AvroSchema.Parse("""{"type":"record","name":"List","fields":[{"name":"next","type":["null","List"]},{"name":"extra","type":"int","default":0}]}""");
        var head = new GenericRecord((RecordSchema)list);
        var current = head;
        for (var i = 1; i < 10; i++)
        {
            var next = new GenericRecord((RecordSchema)list);
            current["next"] = next;
            current = next;
        }

        var chain = GenericDatumWriter.Create(list).WriteToArray(head);
        var deep = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(list, listReader, new GenericDatumReaderOptions { MaxDepth = 5 }).Read(chain));

        await Assert.That(limited.Message).Contains("MaxZeroSizeItems");
        await Assert.That(generous.AsArray().Count).IsEqualTo(90_000);
        await Assert.That(deep.Message).Contains("nested more than 5 levels");
        await Assert.That(GenericDatumReader.Create(list, listReader).Read(chain).AsRecord()["extra"].AsInt32()).IsEqualTo(0);
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

        await Assert.That(array.AsArray().Select(v => v.AsInt64()).ToArray()).IsEquivalentTo(new[] { 1L, 2L, 3L }, CollectionOrdering.Matching);
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

    /// <summary>Apache.Avro 1.12.2's resolving reader overflows the stack building a skip for a recursive record.</summary>
    [Test]
    public async Task WriterOnlyRecursiveFields_AreSkipped()
    {
        var writer = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"int"},{"name":"child","type":["null","Node"]}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"int"}]}""");
        var value = new GenericRecord(writer) { ["v"] = 1, ["child"] = new GenericRecord(writer) { ["v"] = 2, ["child"] = new GenericRecord(writer) { ["v"] = 3, ["child"] = AvroValue.Null } } };

        var read = Resolve(writer, reader, value).AsRecord();

        await Assert.That(read["v"].AsInt32()).IsEqualTo(1);
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
        await Assert.That(TranscodeFails(writer, reader, bytes).Message).Contains("larger than the remaining input");
    }

    [Test]
    public async Task Transcoding_ReadsAPromotedIntAsAnInt_EvenWhenItsVarintIsTooLong()
    {
        // A 5-byte varint beyond the int range: an int reader keeps the low 32 bits, so the promoted long must too.
        // Found by the Resolution fuzz target.
        var writer = AvroSchema.Parse("\"int\"");
        var reader = AvroSchema.Parse("\"long\"");
        byte[] bytes = [0xD2, 0x94, 0xF0, 0xBE, 0x19];

        var resolved = GenericDatumReader.Create(writer, reader).Read(bytes);
        var input = new AvroReader(bytes);
        var transcoded = GenericDatumReader.Create(reader).Read(AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader));

        await Assert.That(transcoded.AsInt64()).IsEqualTo(resolved.AsInt64());
    }

    [Test]
    public async Task Transcoding_LimitsRecordDepth()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"int"},{"name":"next","type":["null","Node"]}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"long"},{"name":"next","type":["null","Node"]}]}""");

        // 200 nested nodes: v = 0 and the union's record branch, then a null to end.
        var bytes = Enumerable.Repeat(new byte[] { 0x00, 0x02 }, 200).SelectMany(b => b).Concat(new byte[] { 0x00, 0x00 }).ToArray();

        await Assert.That(TranscodeFails(writer, reader, bytes).Message).Contains("nested more than 128");
        await Assert.That(Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, reader).Read(bytes)).Message).Contains("nested more than 128");
    }

    [Test]
    public async Task Transcoding_RejectsImpossibleBlockCounts_InKeptArrays()
    {
        var writer = AvroSchema.Parse("""{"type":"array","items":"int"}""");
        var reader = AvroSchema.Parse("""{"type":"array","items":"long"}""");

        byte[] bytes = [0x80, 0xA8, 0xD6, 0xB9, 0x07, 0x02, 0x00];

        await Assert.That(TranscodeFails(writer, reader, bytes).Message).Contains("larger than the remaining input");
    }

    [Test]
    public async Task Transcoding_ReordersNestedRecordFields_RepeatedlyOnOneThread()
    {
        // The reader orders the fields differently at both levels and adds one, so every record takes the reordering
        // path, whose slot arrays are pooled: repeated values must not see a previous value's fields.
        var writer = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Outer","fields":[
              {"name":"a","type":"int"},
              {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"x","type":"string"},{"name":"y","type":"int"}]}},
              {"name":"b","type":"string"}]}
            """);
        var reader = AvroSchema.Parse("""
            {"type":"record","name":"Outer","fields":[
              {"name":"b","type":"string"},
              {"name":"added","type":"int","default":42},
              {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"y","type":"long"},{"name":"x","type":"string"}]}},
              {"name":"a","type":"long"}]}
            """);
        var innerSchema = (RecordSchema)writer.Fields[1].Schema;

        for (var i = 0; i < 3; i++)
        {
            var value = new GenericRecord(writer)
            {
                ["a"] = i,
                ["inner"] = new GenericRecord(innerSchema) { ["x"] = $"x{i}", ["y"] = i * 10 },
                ["b"] = $"b{i}",
            };

            var resolved = Resolve(writer, reader, value).AsRecord();

            await Assert.That(resolved["a"].AsInt64()).IsEqualTo(i);
            await Assert.That(resolved["b"].AsString()).IsEqualTo($"b{i}");
            await Assert.That(resolved["added"].AsInt32()).IsEqualTo(42);
            await Assert.That(resolved["inner"].AsRecord()["x"].AsString()).IsEqualTo($"x{i}");
            await Assert.That(resolved["inner"].AsRecord()["y"].AsInt64()).IsEqualTo(i * 10L);
        }
    }

    [Test]
    public async Task IsSameSchema_ComparesCanonicalForms_AndStaysCorrectWhenReaderSchemasAlternate()
    {
        const string V1 = """{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""";
        var writer = AvroSchema.Parse(V1);
        var sameAsWriter = AvroSchema.Parse(V1);
        var other = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"long"}]}""");

        // Repeated checks, as a reader of generated types makes once per record, alternating between reader schemas.
        for (var i = 0; i < 3; i++)
        {
            await Assert.That(AvroSharp.Serialization.AvroGeneratedCode.IsSameSchema(writer, sameAsWriter)).IsTrue();
            await Assert.That(AvroSharp.Serialization.AvroGeneratedCode.IsSameSchema(writer, other)).IsFalse();
            await Assert.That(AvroSharp.Serialization.AvroGeneratedCode.IsSameSchema(writer, writer)).IsTrue();
        }
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

    private const string TwoEvents = """
        [{"type":"record","name":"Event","namespace":"com.x","fields":[{"name":"x","type":"int"}]},
         {"type":"record","name":"Event","namespace":"com.y","fields":[{"name":"y","type":"string"}]}]
        """;

    /// <summary>
    /// A writer branch is read as the reader branch of its full name, wherever it is in the union, before one of the
    /// same unqualified name (#130). Both unions are parsed separately, as a file's header and the reader's are.
    /// </summary>
    [Test]
    public async Task UnionBranches_OfTheSameUnqualifiedName_ResolveByFullName()
    {
        var writer = (UnionSchema)AvroSchema.Parse(TwoEvents);
        var reader = (UnionSchema)AvroSchema.Parse(TwoEvents);
        var second = new GenericRecord((RecordSchema)writer.Branches[1]) { ["y"] = "hi" };

        var read = Resolve(writer, reader, second).AsRecord();

        await Assert.That(read.Schema).IsSameReferenceAs(reader.Branches[1]);
        await Assert.That(read["y"].AsString()).IsEqualTo("hi");

        // Written again, the value keeps its branch.
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(second);
        await Assert.That(GenericDatumWriter.Create(reader).WriteToArray(read)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnionBranches_ResolveByFullName_InAReorderedReaderUnion()
    {
        var writer = (UnionSchema)AvroSchema.Parse(TwoEvents);
        var reader = AvroSchema.Parse("""
            [{"type":"record","name":"Event","namespace":"com.y","fields":[{"name":"y","type":"string"}]},
             {"type":"record","name":"Event","namespace":"com.x","fields":[{"name":"x","type":"int"}]}]
            """);

        var read = Resolve(writer, reader, new GenericRecord((RecordSchema)writer.Branches[0]) { ["x"] = 5 }).AsRecord();

        await Assert.That(read.Schema.FullName).IsEqualTo("com.x.Event");
        await Assert.That(read["x"].AsInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task EnumBranches_OfTheSameUnqualifiedName_ResolveByFullName()
    {
        const string Json = """["null",{"type":"enum","name":"E","namespace":"a","symbols":["A","B"]},{"type":"enum","name":"E","namespace":"b","symbols":["C","D"]}]""";
        var writer = (UnionSchema)AvroSchema.Parse(Json);

        var read = Resolve(writer, AvroSchema.Parse(Json), AvroValue.FromEnum((EnumSchema)writer.Branches[2], "D"));

        await Assert.That(read.AsEnumSymbol()).IsEqualTo("D");
    }

    [Test]
    public async Task AUnionBranch_WithOnlyTheUnqualifiedName_StillResolves()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"Event","namespace":"com.z","fields":[{"name":"x","type":"int"}]}""");
        var reader = AvroSchema.Parse("""["null",{"type":"record","name":"Event","namespace":"com.x","fields":[{"name":"x","type":"int"}]}]""");

        var read = Resolve(writer, reader, new GenericRecord((RecordSchema)writer) { ["x"] = 7 }).AsRecord();

        await Assert.That(read.Schema.FullName).IsEqualTo("com.x.Event");
        await Assert.That(read["x"].AsInt32()).IsEqualTo(7);
    }

    /// <summary>
    /// Aliases rewrite the writer's schema (the specification), so a reader field's alias takes the writer field before
    /// a reader field of its name does, as in Java (#130).
    /// </summary>
    [Test]
    public async Task AFieldAlias_TakesPrecedenceOverAFieldOfTheWritersName()
    {
        var writer = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
        var reader = AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"a","type":"int","default":-1},
              {"name":"b","type":"int","aliases":["a"],"default":-2}]}
            """);

        var read = Resolve(writer, reader, new GenericRecord(writer) { ["a"] = 5 }).AsRecord();

        await Assert.That(read["a"].AsInt32()).IsEqualTo(-1);
        await Assert.That(read["b"].AsInt32()).IsEqualTo(5);
    }

    /// <summary>
    /// Resolves with the generic reader, and checks that the transcoder generated types use (which writes the reader's
    /// encoding directly) gives the same bytes as the resolved value written again.
    /// </summary>
    private static AvroValue Resolve(AvroSchema writer, AvroSchema reader, AvroValue value)
    {
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(value);
        var resolved = GenericDatumReader.Create(writer, reader).Read(bytes);
        var input = new AvroReader(bytes);
        var transcoded = AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader).ToArray();
        var expected = GenericDatumWriter.Create(reader).WriteToArray(resolved);
        if (!transcoded.AsSpan().SequenceEqual(expected) || !input.IsAtEnd)
        {
            throw new InvalidOperationException($"Transcoded {Convert.ToHexString(transcoded)}, resolved {Convert.ToHexString(expected)}.");
        }

        // The same over one byte per segment: skipping, promotion and the transcoder across segment boundaries (#133).
        var segmented = new AvroReader(AvroSharp.Tests.IO.Segments.ByteByByte(bytes));
        var resolvedFromSegments = GenericDatumWriter.Create(reader).WriteToArray(GenericDatumReader.Create(writer, reader).Read(ref segmented));
        var segmentedInput = new AvroReader(AvroSharp.Tests.IO.Segments.ByteByByte(bytes));
        var transcodedFromSegments = AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref segmentedInput, writer, reader).ToArray();
        if (!resolvedFromSegments.AsSpan().SequenceEqual(expected) || !transcodedFromSegments.AsSpan().SequenceEqual(expected) || !segmented.IsAtEnd || !segmentedInput.IsAtEnd)
        {
            throw new InvalidOperationException($"From segments: resolved {Convert.ToHexString(resolvedFromSegments)}, transcoded {Convert.ToHexString(transcodedFromSegments)}, expected {Convert.ToHexString(expected)}.");
        }

        return resolved;
    }

    private static AvroDataException TranscodeFails(AvroSchema writer, AvroSchema reader, byte[] bytes) =>
        Assert.Throws<AvroDataException>(() =>
        {
            var input = new AvroReader(bytes);
            AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader);
        });
}
