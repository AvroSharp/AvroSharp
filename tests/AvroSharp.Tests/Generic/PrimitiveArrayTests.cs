using System;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Tests.IO;

namespace AvroSharp.Tests.Generic;

/// <summary>Arrays of boolean, int, long, float and double items, which the generic model keeps as the primitives themselves.</summary>
public class PrimitiveArrayTests
{
    [Test]
    public async Task Reader_StoresPrimitiveItemsAsTypedArrays()
    {
        await Assert.That(await Read<long>("long", [1L, -2L, long.MaxValue, long.MinValue], v => v, (AvroValue a, out ReadOnlyMemory<long> m) => a.TryGetInt64Array(out m)))
            .IsEquivalentTo(new[] { 1L, -2L, long.MaxValue, long.MinValue });
        await Assert.That(await Read<int>("int", [0, 63, -64, int.MaxValue, int.MinValue], v => v, (AvroValue a, out ReadOnlyMemory<int> m) => a.TryGetInt32Array(out m)))
            .IsEquivalentTo(new[] { 0, 63, -64, int.MaxValue, int.MinValue });
        await Assert.That(await Read<double>("double", [1.5, double.NaN, double.NegativeInfinity], v => v, (AvroValue a, out ReadOnlyMemory<double> m) => a.TryGetDoubleArray(out m)))
            .IsEquivalentTo(new[] { 1.5, double.NaN, double.NegativeInfinity });
        await Assert.That(await Read<float>("float", [-0.5f, float.Epsilon], v => v, (AvroValue a, out ReadOnlyMemory<float> m) => a.TryGetSingleArray(out m)))
            .IsEquivalentTo(new[] { -0.5f, float.Epsilon });
        await Assert.That(await Read<bool>("boolean", [true, false, true], v => v, (AvroValue a, out ReadOnlyMemory<bool> m) => a.TryGetBooleanArray(out m)))
            .IsEquivalentTo(new[] { true, false, true });
    }

    [Test]
    public async Task Reader_JoinsBlocks_AndReadsEmptyArrays()
    {
        // Three blocks: one with a byte size (a negative count), then two plain ones.
        var bytes = BinaryEncodingTests.EncodeBytes((ref AvroWriter w) =>
        {
            w.WriteLong(-2);
            w.WriteLong(3);
            w.WriteLong(1);
            w.WriteLong(300);
            w.WriteBlockCount(3);
            w.WriteLong(-1);
            w.WriteLong(0);
            w.WriteLong(long.MaxValue);
            w.WriteBlockCount(1);
            w.WriteLong(5);
            w.WriteBlockEnd();
        });
        var reader = GenericDatumReader.Create(AvroSchema.Parse("""{"type":"array","items":"long"}"""));
        var value = reader.Read(bytes);
        await Assert.That(value.TryGetInt64Array(out var items)).IsTrue();
        await Assert.That(items.ToArray()).IsEquivalentTo(new[] { 1L, 300L, -1L, 0L, long.MaxValue, 5L });
        await Assert.That(value.AsArray().Select(v => v.AsInt64())).IsEquivalentTo(items.ToArray());

        // The same input split into one-byte segments takes the reader's per-item paths.
        await Assert.That(ReadSegmented(reader, bytes)).IsEqualTo(value);

        var empty = reader.Read([0]);
        await Assert.That(empty.TryGetInt64Array(out var none)).IsTrue();
        await Assert.That(none.Length).IsEqualTo(0);
        await Assert.That(empty.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reader_KeepsListsForOtherItems_AndPromotedItems()
    {
        var strings = GenericDatumReader.Create(AvroSchema.Parse("""{"type":"array","items":"string"}"""))
            .Read(GenericDatumWriter.Create(AvroSchema.Parse("""{"type":"array","items":"string"}""")).WriteToArray(AvroValue.FromArray(new AvroValue[] { "a" })));
        await Assert.That(strings.TryGetInt64Array(out _)).IsFalse();
        await Assert.That(strings.AsArray()[0].AsString()).IsEqualTo("a");

        // An int array read as a long array is promoted item by item.
        var writer = AvroSchema.Parse("""{"type":"array","items":"int"}""");
        var reader = AvroSchema.Parse("""{"type":"array","items":"long"}""");
        var promoted = GenericDatumReader.Create(writer, reader).Read(GenericDatumWriter.Create(writer).WriteToArray(AvroValue.FromInt32Array(new[] { 1, -2 })));
        await Assert.That(promoted.AsArray()).IsEquivalentTo(new AvroValue[] { 1L, -2L });
    }

    [Test]
    public async Task Writer_EncodesTypedArraysLikeLists()
    {
        await AssertSameEncoding("long", AvroValue.FromInt64Array(new[] { 0L, -1L, 1L << 40, long.MinValue }));
        await AssertSameEncoding("int", AvroValue.FromInt32Array(new[] { 0, -1, 1 << 20, int.MinValue }));
        await AssertSameEncoding("double", AvroValue.FromDoubleArray(new[] { 0.0, -1.5, double.NaN }));
        await AssertSameEncoding("float", AvroValue.FromSingleArray(new[] { 0.0f, -1.5f, float.MaxValue }));
        await AssertSameEncoding("boolean", AvroValue.FromBooleanArray(new[] { true, false, false, true }));
        await AssertSameEncoding("long", AvroValue.FromInt64Array(ReadOnlyMemory<long>.Empty));

        // Items the schema takes after a promotion go item by item, as they would from a list.
        await AssertSameEncoding("long", AvroValue.FromInt32Array(new[] { 7, int.MinValue }));
        await AssertSameEncoding("double", AvroValue.FromInt32Array(new[] { 7, -8 }));
        await AssertSameEncoding("double", AvroValue.FromSingleArray(new[] { 0.25f }));

        // A slice is written as the slice.
        var slice = new long[] { 1, 2, 3, 4 }.AsMemory(1, 2);
        await AssertSameEncoding("long", AvroValue.FromInt64Array(slice));
        await Assert.That(AvroValue.FromInt64Array(slice).AsArray()).IsEquivalentTo(new AvroValue[] { 2L, 3L });
    }

    [Test]
    public async Task Writer_RejectsItemsTheSchemaDoesNotTake()
    {
        var ints = GenericDatumWriter.Create(AvroSchema.Parse("""{"type":"array","items":"int"}"""));
        Assert.Throws<AvroException>(() => ints.WriteToArray(AvroValue.FromInt64Array(new[] { 1L })));
        Assert.Throws<AvroException>(() => ints.WriteToArray(AvroValue.FromBooleanArray(new[] { true })));

        // An empty array has no items to check.
        await Assert.That(ints.WriteToArray(AvroValue.FromInt64Array(ReadOnlyMemory<long>.Empty))).IsEquivalentTo(new byte[] { 0 });
    }

    [Test]
    public async Task Equality_ComparesItems_WhateverTheStorage()
    {
        var typed = AvroValue.FromInt64Array(new[] { 1L, 2L });
        await Assert.That(typed).IsEqualTo(AvroValue.FromInt64Array(new[] { 1L, 2L }));
        await Assert.That(typed).IsEqualTo(AvroValue.FromArray(new AvroValue[] { 1L, 2L }));
        await Assert.That(AvroValue.FromArray(new AvroValue[] { 1L, 2L })).IsEqualTo(typed);
        await Assert.That(typed).IsNotEqualTo(AvroValue.FromInt64Array(new[] { 1L, 3L }));
        await Assert.That(typed).IsNotEqualTo(AvroValue.FromInt64Array(new[] { 1L }));

        // Items of different kinds differ, as single values do.
        await Assert.That(typed).IsNotEqualTo(AvroValue.FromInt32Array(new[] { 1, 2 }));

        // Floating-point items compare by bit pattern, as single values do.
        await Assert.That(AvroValue.FromDoubleArray(new[] { double.NaN })).IsEqualTo(AvroValue.FromDoubleArray(new[] { double.NaN }));
        await Assert.That(AvroValue.FromDoubleArray(new[] { 0.0 })).IsNotEqualTo(AvroValue.FromDoubleArray(new[] { -0.0 }));
        await Assert.That(AvroValue.FromSingleArray(new[] { float.NaN })).IsEqualTo(AvroValue.FromArray(new AvroValue[] { float.NaN }));
    }

    [Test]
    public async Task Accessors_AndKind()
    {
        var booleans = AvroValue.FromBooleanArray(new[] { true });
        await Assert.That(booleans.Kind).IsEqualTo(AvroValueKind.Array);
        await Assert.That(booleans.AsArray()[0].AsBoolean()).IsTrue();
        await Assert.That(booleans.ToString()).IsEqualTo("[true]");
        await Assert.That(booleans.TryGetBooleanArray(out var memory) && memory.Span[0]).IsTrue();
        await Assert.That(booleans.TryGetInt32Array(out _)).IsFalse();
        await Assert.That(AvroValue.FromInt64(1).TryGetInt64Array(out _)).IsFalse();
        await Assert.That(AvroValue.FromArray(new AvroValue[] { 1L }).TryGetInt64Array(out _)).IsFalse();
        await Assert.That(AvroValue.FromDoubleArray(new[] { 2.5 }).AsArray().ToArray()).IsEquivalentTo(new AvroValue[] { 2.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = AvroValue.FromInt32Array(new[] { 1 }).AsArray()[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = AvroValue.FromInt32Array(new[] { 1 }).AsArray()[-1]);

        // The memory is not copied.
        var items = new[] { 1, 2 };
        _ = AvroValue.FromInt32Array(items).TryGetInt32Array(out var wrapped);
        items[0] = 9;
        await Assert.That(wrapped.Span[0]).IsEqualTo(9);
    }

    [Test]
    public async Task InvalidBooleanItem_Throws()
    {
        var schema = AvroSchema.Parse("""{"type":"array","items":"boolean"}""");
        var bytes = new byte[] { 0x06, 0x01, 0x00, 0x02, 0x00 };
        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(schema).Read(bytes));
        await Assert.That(ex.Message).IsEqualTo("Invalid boolean byte 0x02 at offset 3; expected 0 or 1.");
        ex = Assert.Throws<AvroDataException>(() => ReadSegmented(GenericDatumReader.Create(schema), bytes));
        await Assert.That(ex.Message).IsEqualTo("Invalid boolean byte 0x02 at offset 3; expected 0 or 1.");
    }

    private static AvroValue ReadSegmented(GenericDatumReader reader, byte[] bytes)
    {
        var input = new AvroReader(Segments.ByteByByte(bytes));
        return reader.Read(ref input);
    }

    private delegate bool TryGet<T>(AvroValue value, out ReadOnlyMemory<T> items);

    private static async Task<T[]> Read<T>(string items, T[] values, Func<T, AvroValue> toValue, TryGet<T> tryGet)
    {
        var schema = AvroSchema.Parse($$"""{"type":"array","items":"{{items}}"}""");
        var bytes = GenericDatumWriter.Create(schema).WriteToArray(AvroValue.FromArray(values.Select(toValue).ToArray()));
        var value = GenericDatumReader.Create(schema).Read(bytes);
        await Assert.That(value.Kind).IsEqualTo(AvroValueKind.Array);
        await Assert.That(tryGet(value, out var memory)).IsTrue();
        await Assert.That(value.AsArray()).IsEquivalentTo(values.Select(toValue).ToArray());
        return memory.ToArray();
    }

    private static async Task AssertSameEncoding(string items, AvroValue typed)
    {
        var writer = GenericDatumWriter.Create(AvroSchema.Parse($$"""{"type":"array","items":"{{items}}"}"""));
        var asList = AvroValue.FromArray(typed.AsArray().ToList());
        await Assert.That(Convert.ToHexString(writer.WriteToArray(typed))).IsEqualTo(Convert.ToHexString(writer.WriteToArray(asList)));
    }
}
