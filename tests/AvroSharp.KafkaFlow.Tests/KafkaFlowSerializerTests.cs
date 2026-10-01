using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using AvroSharp.Confluent.Tests;
using Confluent.SchemaRegistry;
using KafkaFlow;
using KafkaFlow.Serializer.SchemaRegistry;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.KafkaFlow.Tests;

/// <summary>
/// AvroSharp.KafkaFlow against KafkaFlow's own Confluent Avro serializer (KafkaFlow.Serializer.SchemaRegistry.ConfluentAvro,
/// on Apache.Avro), on one registry: the same bytes, and each reads what the other writes.
/// </summary>
public class KafkaFlowSerializerTests
{
    [Test]
    public async Task WritesTheBytesOfKafkaFlowsConfluentAvroSerializer()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var theirs = await Serialize(new ConfluentAvroSerializer(new RegistryResolver(registry)), NewOrder(), topic);
        var ours = await Serialize(new AvroSharpKafkaFlowSerializer(registry), NewOrder(), topic);

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task EachReadsWhatTheOtherWrites()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirs = await Serialize(new ConfluentAvroSerializer(new RegistryResolver(registry)), NewOrder(), topic);
        var ours = await Serialize(new AvroSharpKafkaFlowSerializer(registry), NewOrder(), topic);

        var readByUs = (Order)await new AvroSharpKafkaFlowDeserializer(registry).DeserializeAsync(new MemoryStream(theirs), typeof(Order), new SerializerContext(topic));
        var readByThem = (Order)await new ConfluentAvroDeserializer(new RegistryResolver(registry)).DeserializeAsync(new MemoryStream(ours), typeof(Order), new SerializerContext(topic));

        await AssertIsNewOrder(readByUs);
        await AssertIsNewOrder(readByThem);
    }

    [Test]
    public async Task SeveralTypesOnOneTopic_ResolveToTheirTypes()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpKafkaFlowSerializer(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });
        var order = await Serialize(serializer, NewOrder(), topic);
        var cart = await Serialize(serializer, new Cart { Owner = "Ada", Lines = [new CartLine { Sku = "SKU-1", Quantity = 1 }] }, topic);
        var listed = new AvroSharpMessageTypeResolver(registry, [typeof(Order), typeof(Cart)]);
#pragma warning disable IL2026 // The test types are in this assembly, which isn't trimmed.
        var found = new AvroSharpMessageTypeResolver(registry);
#pragma warning restore IL2026

        await Assert.That(await registry.GetAllSubjectsAsync()).IsEquivalentTo([$"{topic}-test.shop.Order", $"{topic}-test.shop.Cart"], CollectionOrdering.Any);
        await Assert.That(await listed.OnConsumeAsync(new ConsumedMessage(order))).IsEqualTo(typeof(Order));
        await Assert.That(await listed.OnConsumeAsync(new ConsumedMessage(cart))).IsEqualTo(typeof(Cart));
        await Assert.That(await found.OnConsumeAsync(new ConsumedMessage(order))).IsEqualTo(typeof(Order));
        await Assert.That(await found.OnConsumeAsync(new ConsumedMessage(cart))).IsEqualTo(typeof(Cart));
        var readCart = (Cart)await new AvroSharpKafkaFlowDeserializer(registry).DeserializeAsync(new MemoryStream(cart), typeof(Cart), new SerializerContext(topic));
        await Assert.That(readCart.Lines.Single().Sku).IsEqualTo("SKU-1");
    }

    [Test]
    public async Task Resolver_ReportsWhatItCantResolve()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var cart = await Serialize(new AvroSharpKafkaFlowSerializer(registry), new Cart { Owner = "Ada" }, topic);
        var text = await Serialize(new AvroSharpKafkaFlowSerializer(registry), "text", Topic());
        var onlyOrders = new AvroSharpMessageTypeResolver(registry, [typeof(Order)]);

        await Assert.That(async () => await onlyOrders.OnConsumeAsync(new ConsumedMessage(cart))).Throws<InvalidOperationException>().WithMessageContaining("test.shop.Cart", StringComparison.Ordinal);
        await Assert.That(async () => await onlyOrders.OnConsumeAsync(new ConsumedMessage(text))).Throws<InvalidOperationException>().WithMessageContaining("not a record", StringComparison.Ordinal);
        await Assert.That(async () => await onlyOrders.OnConsumeAsync(new ConsumedMessage([1, 2, 3]))).Throws<InvalidOperationException>().WithMessageContaining("framing", StringComparison.Ordinal);
        await Assert.That(() => new AvroSharpMessageTypeResolver(registry, [typeof(string)])).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpMessageTypeResolver(registry, [typeof(Order), typeof(Order)])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Serializer_RejectsWhatKafkaFlowCantCarry()
    {
        using var registry = new InMemorySchemaRegistry();

        await Assert.That(() => new AvroSharpKafkaFlowSerializer(registry, new AvroSharpSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header })).Throws<ArgumentException>();
        await Assert.That(async () => await Serialize(new AvroSharpKafkaFlowSerializer(registry), new Uri("https://example.com"), Topic())).Throws<InvalidOperationException>();
    }

    internal static async Task AssertIsNewOrder(Order order)
    {
        var expected = NewOrder();
        await Assert.That(order.Id).IsEqualTo(expected.Id);
        await Assert.That(order.Customer).IsEqualTo(expected.Customer);
        await Assert.That(order.Total).IsEqualTo(expected.Total);
        await Assert.That(order.Items.Select(i => i.Sku)).IsEquivalentTo(expected.Items.Select(i => i.Sku), CollectionOrdering.Matching);
        await Assert.That(order.Note).IsEqualTo(expected.Note);
    }

    private static async Task<byte[]> Serialize(ISerializer serializer, object message, string topic)
    {
        using var output = new MemoryStream();
        await serializer.SerializeAsync(message, output, new SerializerContext(topic));
        return output.ToArray();
    }
}
