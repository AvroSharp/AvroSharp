using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>AvroValueComparer and AvroBinaryComparer: the specification's sort order (#175).</summary>
public class SortOrderTests
{
    // Every type, ascending, descending and ignored fields, and a map that is ignored.
    private const string EveryTypeSchema = """
        {"type":"record","name":"Row","namespace":"sort","fields":[
          {"name":"nothing","type":"null"},
          {"name":"flag","type":"boolean"},
          {"name":"small","type":"int","order":"descending"},
          {"name":"count","type":"long"},
          {"name":"ratio","type":"float"},
          {"name":"score","type":"double","order":"descending"},
          {"name":"data","type":"bytes"},
          {"name":"name","type":"string"},
          {"name":"color","type":{"type":"enum","name":"Color","symbols":["RED","GREEN","BLUE"]}},
          {"name":"id","type":{"type":"fixed","name":"Id","size":2}},
          {"name":"list","type":{"type":"array","items":["null","int",{"type":"record","name":"Pair","fields":[
            {"name":"a","type":"string"},{"name":"b","type":"long","order":"descending"}]}]}},
          {"name":"tags","type":{"type":"map","values":"long"},"order":"ignore"},
          {"name":"pair","type":"Pair"},
          {"name":"next","type":["null","Row"]}
        ]}
        """;

    public static IEnumerable<(string Schema, AvroValue Smaller, AvroValue Larger)> OrderedPairs()
    {
        yield return ("\"boolean\"", false, true);
        yield return ("\"int\"", -5, 3);
        yield return ("\"long\"", long.MinValue, -1L);
        yield return ("\"long\"", 3, 4L); // an int value widens to long
        yield return ("\"float\"", -0f, 0f);
        yield return ("\"float\"", float.PositiveInfinity, float.NaN);
        yield return ("\"double\"", -1.5, -0d);
        yield return ("\"double\"", double.MaxValue, double.NaN);
        yield return ("\"bytes\"", new byte[] { 0x7F }, new byte[] { 0x80 });
        yield return ("\"bytes\"", new byte[] { 1 }, new byte[] { 1, 0 });

        // U+E000 is three UTF-8 bytes from EE, U+1F600 four from F0; as UTF-16, U+1F600 starts with D83D, before E000.
        yield return ("\"string\"", "", "\U0001F600");
        yield return ("\"string\"", "Z", "a");
        yield return ("\"string\"", "ab", "abc");
        yield return ("""{"type":"array","items":"int"}""", AvroValue.FromArray([1, 2]), AvroValue.FromArray([1, 2, 0]));
        yield return ("""{"type":"array","items":"int"}""", AvroValue.FromArray([1, 9]), AvroValue.FromArray([2]));
        yield return ("""{"type":"array","items":"long"}""", AvroValue.FromInt64Array(new long[] { 1, 2 }), AvroValue.FromArray([1L, 3L]));

        // A union by branch first: any int sorts before any string here.
        yield return ("""["null","int","string"]""", AvroValue.Null, int.MinValue);
        yield return ("""["null","int","string"]""", int.MaxValue, string.Empty);
    }

    [Test]
    [MethodDataSource(nameof(OrderedPairs))]
    public async Task Values_SortAsTheSpecificationSays(string schemaJson, AvroValue smaller, AvroValue larger)
    {
        var schema = AvroSchema.Parse(schemaJson);

        await AssertOrder(schema, smaller, larger, -1);
        await AssertOrder(schema, larger, smaller, 1);
        await AssertOrder(schema, smaller, smaller, 0);
    }

    [Test]
    public async Task Enums_SortBySymbolPosition_NotByName()
    {
        var schema = (EnumSchema)AvroSchema.Parse("""{"type":"enum","name":"Size","symbols":["SMALL","LARGE","MEDIUM"]}""");

        await AssertOrder(schema, AvroValue.FromEnum(schema, "SMALL"), AvroValue.FromEnum(schema, "LARGE"), -1);
        await AssertOrder(schema, AvroValue.FromEnum(schema, "MEDIUM"), AvroValue.FromEnum(schema, "LARGE"), 1);
    }

    [Test]
    public async Task Fixed_SortsByUnsignedBytes()
    {
        var schema = (FixedSchema)AvroSchema.Parse("""{"type":"fixed","name":"F","size":2}""");

        await AssertOrder(schema, new GenericFixed(schema, [0x00, 0xFF]), new GenericFixed(schema, [0x80, 0x00]), -1);
    }

    [Test]
    public async Task Records_HonorDescendingAndIgnoredFields()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"skip","type":"string","order":"ignore"},
              {"name":"down","type":"int","order":"descending"},
              {"name":"up","type":"int"}]}
            """);
        GenericRecord Row(string skip, int down, int up) => new(schema) { ["skip"] = skip, ["down"] = down, ["up"] = up };

        // Ignored: equal although skip differs. Descending: the larger down sorts first. Then up, ascending.
        await AssertOrder(schema, Row("a", 1, 1), Row("b", 1, 1), 0);
        await AssertOrder(schema, Row("a", 2, 9), Row("a", 1, 0), -1);
        await AssertOrder(schema, Row("z", 1, 1), Row("a", 1, 2), -1);
    }

    [Test]
    public async Task Maps_CannotBeCompared_UnlessIgnored()
    {
        var map = AvroSchema.Parse("""{"type":"map","values":"int"}""");
        var empty = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal));

        var valueError = Assert.Throws<AvroException>(() => AvroValueComparer.Create(map).Compare(empty, empty));
        var bytes = GenericDatumWriter.Create(map).WriteToArray(empty);
        var binaryError = Assert.Throws<AvroException>(() => AvroBinaryComparer.Create(map).Compare(bytes, bytes));

        await Assert.That(valueError.Message).Contains("\"order\": \"ignore\"");
        await Assert.That(binaryError.Message).IsEqualTo(valueError.Message);
    }

    [Test]
    public async Task RandomValues_SortTheSameWay_WithBothComparers()
    {
        var schema = AvroSchema.Parse(EveryTypeSchema);
        var writer = GenericDatumWriter.Create(schema);
        var values = AvroRandomData.Generate(schema, seed: 175, new AvroRandomDataOptions { MaxDepth = 3 }).Take(120).ToList();
        var encoded = values.ConvertAll(v => writer.WriteToArray(v));
        var byValue = AvroValueComparer.Create(schema);
        var byBinary = AvroBinaryComparer.Create(schema);

        var mismatches = new List<string>();
        var equal = 0;
        for (var i = 0; i < values.Count; i++)
        {
            for (var j = 0; j < values.Count; j++)
            {
                var expected = byValue.Compare(values[i], values[j]);
                var actual = byBinary.Compare(encoded[i], encoded[j]);
                equal += expected == 0 ? 1 : 0;
                if (expected != actual || expected != -byValue.Compare(values[j], values[i]))
                {
                    mismatches.Add($"{i} vs {j}: value {expected}, binary {actual}");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
        await Assert.That(equal).IsEqualTo(values.Count); // each value only equals itself

        // A sort by either comparer gives the same order.
        var sortedByValue = Enumerable.Range(0, values.Count).OrderBy<int, AvroValue>(i => values[i], byValue).ToList();
        var sortedByBinary = Enumerable.Range(0, values.Count).OrderBy<int, byte[]?>(i => encoded[i], byBinary).ToList();
        await Assert.That(sortedByBinary).IsEquivalentTo(sortedByValue, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task JavasRecords_SortAsJavasBinaryDataCompareSortsThem()
    {
        // sort.avro and sort-order.txt come from Apache Avro Java 1.12.2 (TestData/java-avro/SortOrder.java).
        using var file = AvroFileReader.OpenGeneric(File.OpenRead(TestData.PathOf("java-avro/sort.avro")));
        var schema = file.WriterSchema;
        var records = file.ReadAll().ToList();
        var order = File.ReadAllLines(TestData.PathOf("java-avro/sort-order.txt"));
        var writer = GenericDatumWriter.Create(schema);
        var encoded = records.ConvertAll(v => writer.WriteToArray(v));
        var byValue = AvroValueComparer.Create(schema);
        var byBinary = AvroBinaryComparer.Create(schema);

        await Assert.That(order.Length).IsEqualTo(records.Count);
        var mismatches = new List<string>();
        for (var i = 0; i < records.Count; i++)
        {
            for (var j = 0; j < records.Count; j++)
            {
                var expected = order[i][j] switch { '<' => -1, '>' => 1, _ => 0 };
                var value = byValue.Compare(records[i], records[j]);
                var binary = byBinary.Compare(encoded[i], encoded[j]);
                if (value != expected || binary != expected)
                {
                    mismatches.Add($"{i} vs {j}: Java {expected}, value {value}, binary {binary}");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Binary_RejectsAnInvalidUnionIndex()
    {
        var schema = AvroSchema.Parse("""["null","int"]""");
        var bytes = Encode((ref AvroWriter w) => w.WriteUnionIndex(5));

        var ex = Assert.Throws<AvroDataException>(() => AvroBinaryComparer.Create(schema).Compare(bytes, bytes));

        await Assert.That(ex.Message).Contains("union index 5");
    }

    [Test]
    public async Task Binary_LimitsHowDeepRecordsNest()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]}]}""");
        var bytes = Encode((ref AvroWriter w) =>
        {
            for (var i = 0; i < 20; i++)
            {
                w.WriteUnionIndex(1);
            }

            w.WriteUnionIndex(0);
        });
        var comparer = AvroBinaryComparer.Create(schema, new GenericDatumReaderOptions { MaxDepth = 10 });

        var ex = Assert.Throws<AvroDataException>(() => comparer.Compare(bytes, bytes));

        await Assert.That(ex.Message).Contains("MaxDepth");
        await Assert.That(AvroBinaryComparer.Create(schema).Compare(bytes, bytes)).IsEqualTo(0);
    }

    [Test]
    public async Task Binary_LimitsZeroSizeItems()
    {
        // Nulls take no bytes, so a count of a billion would otherwise be a billion comparisons.
        var schema = AvroSchema.Parse("""{"type":"array","items":"null"}""");
        var bytes = Encode((ref AvroWriter w) => w.WriteBlockCount(1_000_000_000));

        var ex = Assert.Throws<AvroDataException>(() => AvroBinaryComparer.Create(schema).Compare(bytes, bytes));

        await Assert.That(ex.Message).Contains("MaxZeroSizeItems");
    }

    [Test]
    public async Task Binary_RejectsTruncatedData()
    {
        var schema = AvroSchema.Parse("\"string\"");
        var bytes = Encode((ref AvroWriter w) => w.WriteLong(10));

        Assert.Throws<AvroTruncatedDataException>(() => AvroBinaryComparer.Create(schema).Compare(bytes, bytes));
        await Assert.That(bytes.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_SkipsIgnoredFields_WithoutComparingThem()
    {
        // The ignored map comes first: its entries are skipped, block sizes included, before the int decides.
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"R","fields":[
              {"name":"tags","type":{"type":"map","values":"string"},"order":"ignore"},
              {"name":"n","type":"int"}]}
            """);
        var withSizes = Encode((ref AvroWriter w) =>
        {
            w.WriteLong(-1);
            w.WriteLong(4); // the block's size: key "k", value "v"
            w.WriteString("k");
            w.WriteString("v");
            w.WriteBlockEnd();
            w.WriteInt(1);
        });
        var smaller = GenericDatumWriter.Create(schema).WriteToArray(new GenericRecord(schema)
        {
            ["tags"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["a"] = "b", ["c"] = "d" }),
            ["n"] = 0,
        });

        await Assert.That(AvroBinaryComparer.Create(schema).Compare(smaller, withSizes)).IsEqualTo(-1);
        await Assert.That(AvroBinaryComparer.Create(schema).Compare(withSizes, smaller)).IsEqualTo(1);
    }

    [Test]
    public async Task Value_RejectsAValueOfAnotherType()
    {
        var schema = AvroSchema.Parse("\"int\"");

        var ex = Assert.Throws<AvroException>(() => AvroValueComparer.Create(schema).Compare(1, "one"));

        await Assert.That(ex.Message).Contains("String");
    }

    [Test]
    public async Task Value_RejectsARecordThatContainsItself()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]}]}""");
        var node = new GenericRecord(schema);
        node["next"] = node;

        var ex = Assert.Throws<AvroException>(() => AvroValueComparer.Create(schema).Compare(node, node));

        await Assert.That(ex.Message).Contains("contain itself");
    }

    [Test]
    public async Task Comparers_AreCachedPerSchema()
    {
        var schema = AvroSchema.Parse("\"int\"");

        await Assert.That(AvroValueComparer.Create(schema)).IsSameReferenceAs(AvroValueComparer.Create(schema));
        await Assert.That(AvroBinaryComparer.Create(schema)).IsSameReferenceAs(AvroBinaryComparer.Create(schema, GenericDatumReaderOptions.Default));
        await Assert.That(AvroBinaryComparer.Create(schema, new GenericDatumReaderOptions())).IsNotSameReferenceAs(AvroBinaryComparer.Create(schema));
    }

    [Test]
    public async Task BinaryComparer_SortsByteArrays_NullFirst()
    {
        var schema = AvroSchema.Parse("\"int\"");
        IComparer<byte[]?> comparer = AvroBinaryComparer.Create(schema);
        var writer = GenericDatumWriter.Create(schema);
        var arrays = new[] { writer.WriteToArray(5), null, writer.WriteToArray(-3) };

        Array.Sort(arrays, comparer);

        await Assert.That(arrays[0]).IsNull();
        await Assert.That(GenericDatumReader.Create(schema).Read(arrays[1]).AsInt32()).IsEqualTo(-3);
    }

    private static async Task AssertOrder(AvroSchema schema, AvroValue x, AvroValue y, int expected)
    {
        var writer = GenericDatumWriter.Create(schema);
        await Assert.That(AvroValueComparer.Create(schema).Compare(x, y)).IsEqualTo(expected);
        await Assert.That(AvroBinaryComparer.Create(schema).Compare(writer.WriteToArray(x), writer.WriteToArray(y))).IsEqualTo(expected);
    }

    private static byte[] Encode(AvroWriteAction write)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        write(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private delegate void AvroWriteAction(ref AvroWriter writer);
}
