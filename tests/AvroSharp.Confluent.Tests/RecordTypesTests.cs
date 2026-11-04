using System;
using System.Threading.Tasks;
using AvroSharp.IO;
using AvroSharp.Schemas;
using Avro.Specific;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.Confluent.Tests;

/// <summary>Several record types per serializer and deserializer (#192): one topic, many record types.</summary>
public class RecordTypesTests
{
    private static readonly Type[] Types = [typeof(Order), typeof(Cart)];

    private static Cart NewCart() => new() { Owner = "Ada", Lines = [new CartLine { Sku = "SKU-1", Quantity = 3 }] };

    [Test]
    public async Task TwoTypes_WithTopicRecord_RegisterUnderTwoSubjects_WriteConfluentsBytes_AndReadBackAsTheirTypes()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpSerializer<object>(registry, Types, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });
        var confluent = new AvroSerializer<ISpecificRecord>(registry, new AvroSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });

        var order = await serializer.SerializeAsync(NewOrder(), Value(topic));
        var cart = serializer.Serialize(NewCart(), Value(topic));
        var theirOrder = await confluent.SerializeAsync(NewOrder(), Value(topic));
        var theirCart = await confluent.SerializeAsync(NewCart(), Value(topic));

        await Assert.That(await registry.GetAllSubjectsAsync()).Contains($"{topic}-test.shop.Order").And.Contains($"{topic}-test.shop.Cart");
        await Assert.That(order).IsEquivalentTo(theirOrder, CollectionOrdering.Matching);
        await Assert.That(cart).IsEquivalentTo(theirCart, CollectionOrdering.Matching);

        var deserializer = new AvroSharpDeserializer<object>(registry, Types);
        await ConfluentInteropTests.AssertIsNewOrder((Order)await deserializer.DeserializeAsync(order, isNull: false, Value(topic)));
        var readCart = deserializer.Deserialize(theirCart, isNull: false, Value(topic));
        await Assert.That(readCart).IsTypeOf<Cart>();
        await Assert.That(((Cart)readCart).Lines[0].Quantity).IsEqualTo(3);

        // A shared base type or interface works as T too.
        var specific = new AvroSharpDeserializer<ISpecificRecord>(registry, Types);
        await Assert.That(await specific.DeserializeAsync(cart, isNull: false, Value(topic))).IsTypeOf<Cart>();
        await Assert.That(await serializer.SerializeAsync(null!, Value(topic))).IsNull();
    }

    [Test]
    public async Task ATopLevelUnionOfRecords_UnderOneSubject_ReadsEachBranchAsItsType()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var union = new UnionSchema([Order.AvroTypeInfo.Schema, Cart.AvroTypeInfo.Schema]);
        var id = await registry.RegisterSchemaAsync(topic + "-value", new global::Confluent.SchemaRegistry.Schema(union.ToJson(), SchemaType.Avro));
        var deserializer = new AvroSharpDeserializer<object>(registry, Types);

        var order = await deserializer.DeserializeAsync(UnionMessage(id, 0, (ref AvroWriter w) => Order.AvroTypeInfo.Write(ref w, NewOrder())), isNull: false, Value(topic));
        var cart = await deserializer.DeserializeAsync(UnionMessage(id, 1, (ref AvroWriter w) => Cart.AvroTypeInfo.Write(ref w, NewCart())), isNull: false, Value(topic));

        await ConfluentInteropTests.AssertIsNewOrder((Order)order);
        await Assert.That(((Cart)cart).Owner).IsEqualTo("Ada");
    }

    [Test]
    public async Task TypesOutsideTheList_AndBadLists_AreRefused()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpSerializer<object>(registry, [typeof(Order)], new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });
        var cart = await new AvroSharpSerializer<Cart>(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord }).SerializeAsync(NewCart(), Value(topic));
        var orders = new AvroSharpDeserializer<object>(registry, [typeof(Order)]);

        await Assert.That(async () => await serializer.SerializeAsync(NewCart(), Value(topic))).Throws<InvalidOperationException>().WithMessageContaining("test.shop.Cart", StringComparison.Ordinal);
        await Assert.That(async () => await orders.DeserializeAsync(cart, isNull: false, Value(topic))).Throws<AvroSchemaException>().WithMessageContaining("test.shop.Cart", StringComparison.Ordinal);
        await Assert.That(() => new AvroSharpSerializer<object>(registry, Array.Empty<Type>())).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpSerializer<object>(registry, [typeof(Order), typeof(Order)])).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpDeserializer<ISpecificRecord>(registry, [typeof(Order), typeof(Customer)])).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpDeserializer<object>(registry, [typeof(string)])).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpDeserializer<object>(registry, [typeof(Uri)])).Throws<InvalidOperationException>();
    }

    private delegate void WriteBody(ref AvroWriter writer);

    // A message of a top-level union: the framing, the branch, then the record.
    private static byte[] UnionMessage(int id, int branch, WriteBody body)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new AvroWriter(buffer);
        writer.WriteLong(branch);
        body(ref writer);
        writer.Flush();
        return [0, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, .. buffer.WrittenSpan.ToArray()];
    }
}
