using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using TUnit.Assertions.Enums;

namespace AvroSharp.Tests.Generic;

public class AvroValueTests
{
    [Test]
    public async Task Kinds_AndAccessors()
    {
        var enumSchema = new EnumSchema(new SchemaName("E"), ["A", "B"]);
        var fixedSchema = new FixedSchema(new SchemaName("F"), 2);
        var recordSchema = new RecordSchema(new SchemaName("R"), []);

        await Assert.That(AvroValue.Null.Kind).IsEqualTo(AvroValueKind.Null);
        await Assert.That(default(AvroValue).IsNull).IsTrue();
        await Assert.That(AvroValue.FromBoolean(true).AsBoolean()).IsTrue();
        await Assert.That(AvroValue.FromInt32(-7).AsInt32()).IsEqualTo(-7);
        await Assert.That(AvroValue.FromInt32(-7).AsInt64()).IsEqualTo(-7L);
        await Assert.That(AvroValue.FromInt64(long.MinValue).AsInt64()).IsEqualTo(long.MinValue);
        await Assert.That(AvroValue.FromSingle(1.5f).AsSingle()).IsEqualTo(1.5f);
        await Assert.That(AvroValue.FromSingle(1.5f).AsDouble()).IsEqualTo(1.5);
        await Assert.That(AvroValue.FromDouble(Math.E).AsDouble()).IsEqualTo(Math.E);
        await Assert.That(AvroValue.FromString("s").AsString()).IsEqualTo("s");
        await Assert.That(AvroValue.FromBytes([1, 2]).AsBytes()).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(AvroValue.FromEnum(enumSchema, 1).AsEnumSymbol()).IsEqualTo("B");
        await Assert.That(AvroValue.FromEnum(enumSchema, "A").AsEnumOrdinal()).IsEqualTo(0);
        await Assert.That(AvroValue.FromEnum(enumSchema, "A").EnumSchema).IsSameReferenceAs(enumSchema);
        await Assert.That(AvroValue.FromFixed(new GenericFixed(fixedSchema, [1, 2])).Kind).IsEqualTo(AvroValueKind.Fixed);
        await Assert.That(AvroValue.FromRecord(new GenericRecord(recordSchema)).Kind).IsEqualTo(AvroValueKind.Record);
        await Assert.That(AvroValue.FromArray(new AvroValue[] { 1 }).Kind).IsEqualTo(AvroValueKind.Array);
        await Assert.That(AvroValue.FromMap(new Dictionary<string, AvroValue>()).Kind).IsEqualTo(AvroValueKind.Map);
        await Assert.That(AvroValue.FromString(null).IsNull).IsTrue();
    }

    [Test]
    public async Task WrongKind_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AvroValue.FromInt64(1).AsInt32());
        Assert.Throws<InvalidOperationException>(() => AvroValue.FromString("s").AsInt64());
        Assert.Throws<InvalidOperationException>(() => AvroValue.FromDouble(1).AsSingle());
        Assert.Throws<InvalidOperationException>(() => AvroValue.Null.AsString());
        var ex = Assert.Throws<InvalidOperationException>(() => AvroValue.FromInt32(1).AsArray());
        await Assert.That(ex.Message).IsEqualTo("The value is Int, not Array.");
    }

    [Test]
    public async Task Enum_RejectsUnknownSymbolsAndOrdinals()
    {
        var schema = new EnumSchema(new SchemaName("E"), ["A"]);
        Assert.Throws<ArgumentException>(() => AvroValue.FromEnum(schema, "B"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AvroValue.FromEnum(schema, 1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Equality_IsStructural()
    {
        await Assert.That(AvroValue.FromBytes([1, 2])).IsEqualTo(AvroValue.FromBytes([1, 2]));
        await Assert.That(AvroValue.FromDouble(double.NaN)).IsEqualTo(AvroValue.FromDouble(double.NaN));
        await Assert.That(AvroValue.FromInt32(1) == AvroValue.FromInt64(1)).IsFalse();
        await Assert.That(AvroValue.FromArray(new AvroValue[] { 1, "a" })).IsEqualTo(AvroValue.FromArray(new List<AvroValue> { 1, "a" }));
        await Assert.That(AvroValue.FromMap(new Dictionary<string, AvroValue> { ["a"] = 1, ["b"] = 2 }))
            .IsEqualTo(AvroValue.FromMap(new Dictionary<string, AvroValue> { ["b"] = 2, ["a"] = 1 }));
        await Assert.That(AvroValue.FromMap(new Dictionary<string, AvroValue> { ["a"] = 1 }) != AvroValue.FromMap(new Dictionary<string, AvroValue> { ["a"] = 2 })).IsTrue();
    }

    [Test]
    public async Task ToObject_AndToString()
    {
        var schema = new EnumSchema(new SchemaName("E"), ["A"]);
        await Assert.That(AvroValue.FromInt64(5).ToObject()).IsEqualTo(5L);
        await Assert.That(AvroValue.FromEnum(schema, 0).ToObject()).IsEqualTo("A");
        await Assert.That(AvroValue.Null.ToObject()).IsNull();
        await Assert.That(AvroValue.FromArray(new AvroValue[] { 1, AvroValue.Null }).ToString()).IsEqualTo("[1, null]");
    }

    [Test]
    public async Task GenericRecord_FieldAccess()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"string"}]}""");
        var record = new GenericRecord(schema) { ["b"] = "x" };
        record[0] = 5;

        await Assert.That(record["a"].AsInt32()).IsEqualTo(5);
        await Assert.That(record.TryGetValue("b", out var b) && string.Equals(b.AsString(), "x", StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.TryGetValue("c", out _)).IsFalse();
        await Assert.That(record.TryGetValue(0, out var a) && a.AsInt32() == 5).IsTrue();
        await Assert.That(record.TryGetValue(1, out var byIndex) && string.Equals(byIndex.AsString(), "x", StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.TryGetValue(2, out var missing) || missing.Kind != AvroValueKind.Null).IsFalse();
        await Assert.That(record.TryGetValue(-1, out _)).IsFalse();
        await Assert.That(record.TryGetValue(int.MinValue, out _)).IsFalse();
        Assert.Throws<KeyNotFoundException>(() => _ = record["c"]);
        await Assert.That(record.ToString()).IsEqualTo("R {a: 5, b: x}");
    }

    [Test]
    public async Task GenericFixed_ChecksTheSize()
    {
        var schema = new FixedSchema(new SchemaName("F"), 2);
        Assert.Throws<ArgumentException>(() => new GenericFixed(schema, [1]));
        await Assert.That(new GenericFixed(schema, [1, 2]).Bytes.Length).IsEqualTo(2);
    }

    /// <summary>Records compare doubles by bits, so a NaN field does not make a record unequal to its copy, and equal records hash alike.</summary>
    [Test]
    public async Task RecordsWithANaNField_AreEqual_AndHashAlike()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"d","type":"double"}]}""");
        var left = (AvroValue)new GenericRecord(schema) { ["d"] = double.NaN };
        var right = (AvroValue)new GenericRecord(schema) { ["d"] = double.NaN };

        await Assert.That(left).IsEqualTo(right);
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    /// <summary>
    /// The generic reader keeps an int array as a typed primitive array; it still equals, and hashes like, the same
    /// values built as a list of AvroValues.
    /// </summary>
    [Test]
    public async Task ATypedArrayFromTheReader_EqualsTheSameValuesAsAList()
    {
        var schema = AvroSchema.Parse("""{"type":"array","items":"int"}""");
        var list = AvroValue.FromArray(new List<AvroValue> { 1, -2, 300 });
        var read = GenericDatumReader.Create(schema).Read(GenericDatumWriter.Create(schema).WriteToArray(list));

        await Assert.That(read).IsEqualTo(list);
        await Assert.That(list).IsEqualTo(read);
        await Assert.That(read.GetHashCode()).IsEqualTo(list.GetHashCode());
    }
}
