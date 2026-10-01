using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avro.Generic;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// AvroSharp's serde against Confluent's own Avro serde (Apache.Avro) on one registry: the same subject, schema text,
/// schema ID and bytes, and each reads what the other writes.
/// </summary>
public class ConfluentInteropTests
{
    [Test]
    public async Task GeneratedType_WritesConfluentsBytes_AndRegistersTheSameSchema()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var order = NewOrder();

        var theirs = await new AvroSerializer<Order>(registry).SerializeAsync(order, Value(topic));
        var ours = await new AvroSharpSerializer<Order>(registry).SerializeAsync(order, Value(topic));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(ours[0]).IsEqualTo((byte)0);
        // The registry has one version: AvroSharp registered the schema Confluent did.
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task GeneratedType_ReadsWhatConfluentWrites()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        var order = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await AssertIsNewOrder(order);
    }

    [Test]
    public async Task ConfluentReadsWhatAvroSharpWrites_SpecificAndGeneric()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        var specific = await new AvroDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        var generic = await new AvroDeserializer<GenericRecord>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await AssertIsNewOrder(specific);
        await Assert.That(generic["customer"]).IsEqualTo("Ada");
        await Assert.That(((GenericEnum)generic["status"]).Value).IsEqualTo("PAID");
        await Assert.That((object[])generic["items"]).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Primitives_MatchConfluent()
    {
        await AssertPrimitive("text");
        await AssertPrimitive(42);
        await AssertPrimitive(-1234567890123L);
        await AssertPrimitive(1.5f);
        await AssertPrimitive(Math.PI);
        await AssertPrimitive(true);
        await AssertPrimitive(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task Key_UsesTheKeySubject()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var theirs = await new AvroSerializer<string>(registry).SerializeAsync("k", Key(topic));
        var ours = await new AvroSharpSerializer<string>(registry).SerializeAsync("k", Key(topic));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(await registry.GetAllSubjectsAsync()).IsEquivalentTo(new[] { topic + "-key" }, CollectionOrdering.Matching);
        await Assert.That(await new AvroSharpDeserializer<string>(registry).DeserializeAsync(theirs, isNull: false, Key(topic))).IsEqualTo("k");
    }

    [Test]
    public async Task RecordNameStrategy_MatchesConfluent()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var order = NewOrder();

        var theirs = await new AvroSerializer<Order>(registry, new AvroSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record }).SerializeAsync(order, Value(topic));
        var ours = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record }).SerializeAsync(order, Value(topic));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(await registry.GetAllSubjectsAsync()).IsEquivalentTo(new[] { "test.shop.Order" }, CollectionOrdering.Matching);
        var back = await new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record }).DeserializeAsync(theirs, isNull: false, Value(topic));
        await AssertIsNewOrder(back);
    }

    [Test]
    public async Task OlderWriter_ReadsAsTheCurrentType_WithDefaults()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var v1 = (Avro.RecordSchema)Avro.Schema.Parse(OrderV1);
        var old = new GenericRecord(v1);
        old.Add("id", Guid.NewGuid());
        old.Add("customer", "from v1");
        old.Add("placed_at", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        old.Add("total", new Avro.AvroDecimal(5.00m));
        old.Add("items", Array.Empty<object>());
        var bytes = await new AvroSerializer<GenericRecord>(registry).SerializeAsync(old, Value(topic));

        var order = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(order.Customer).IsEqualTo("from v1");
        await Assert.That(order.Status).IsEqualTo(Status.NEW);
        await Assert.That(order.Tags).IsEmpty();
        await Assert.That(order.Note).IsNull();
    }

    [Test]
    public async Task NewerWriter_ReadsAsAnOlderType()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        var v1 = AvroSharp.Schemas.AvroSchema.Parse(OrderV1);
        var value = await AvroSharpGeneric.CreateDeserializer(registry, v1).DeserializeAsync(bytes, isNull: false, Value(topic));

        var record = value.AsRecord();
        await Assert.That(record.Schema.Fields.Select(f => f.Name)).IsEquivalentTo(new[] { "id", "customer", "placed_at", "total", "items" }, CollectionOrdering.Matching);
        await Assert.That(record["customer"].AsString()).IsEqualTo("Ada");
    }

    [Test]
    public async Task NullValue_IsNoMessageBody()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var bytes = await new AvroSharpSerializer<Order>(registry).SerializeAsync(null!, Value(topic));
        var value = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic));

        await Assert.That(bytes).IsNull();
        await Assert.That(value).IsNull();
        await Assert.That(await registry.GetAllSubjectsAsync()).IsEmpty();
    }

    internal static async Task AssertIsNewOrder(Order order)
    {
        var expected = NewOrder();
        await Assert.That(order.Id).IsEqualTo(expected.Id);
        await Assert.That(order.Customer).IsEqualTo(expected.Customer);
        await Assert.That(order.PlacedAt).IsEqualTo(expected.PlacedAt);
        await Assert.That(order.Total).IsEqualTo(expected.Total);
        await Assert.That(order.Status).IsEqualTo(expected.Status);
        await Assert.That(order.Items.Select(i => (i.Sku, i.Quantity))).IsEquivalentTo(expected.Items.Select(i => (i.Sku, i.Quantity)), CollectionOrdering.Matching);
        await Assert.That(order.Tags).IsEquivalentTo(expected.Tags, CollectionOrdering.Any);
        await Assert.That(order.Note).IsEqualTo(expected.Note);
    }

    private static async Task AssertPrimitive<T>(T value)
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var theirs = await new AvroSerializer<T>(registry).SerializeAsync(value, Value(topic));
        var ours = await new AvroSharpSerializer<T>(registry).SerializeAsync(value, Value(topic));
        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching).Because(typeof(T).Name);
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1).Because(typeof(T).Name);
        var back = await new AvroSharpDeserializer<T>(registry).DeserializeAsync(theirs, isNull: false, Value(topic));
        var theirsBack = await new AvroDeserializer<T>(registry).DeserializeAsync(ours, isNull: false, Value(topic));
        await Assert.That(EqualityComparer<T>.Default.Equals(back, value) || (value is byte[] b && ((byte[])(object)back!).SequenceEqual(b))).IsTrue().Because(typeof(T).Name);
        await Assert.That(EqualityComparer<T>.Default.Equals(theirsBack, value) || (value is byte[] c && ((byte[])(object)theirsBack!).SequenceEqual(c))).IsTrue().Because(typeof(T).Name);
    }
}
