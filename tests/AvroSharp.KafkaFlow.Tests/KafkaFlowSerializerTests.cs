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
        await Assert.That(() => new AvroSharpMessageTypeResolver(registry, [])).Throws<ArgumentException>().WithMessageContaining("No message types", StringComparison.Ordinal);
    }

    // Confluent's "several types under the topic subject": the subject's schema is a top-level union of records, and
    // each message's union branch names its record.
    [Test]
    public async Task ATopLevelUnion_ResolvesEachMessageToItsBranch()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var union = Schemas.AvroSchema.Parse("""
            [{"type":"record","name":"Cart","namespace":"test.shop","fields":[
               {"name":"owner","type":"string"},
               {"name":"lines","type":{"type":"array","items":{"type":"record","name":"CartLine","fields":[
                 {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]},
             "test.shop.CartLine"]
            """);
        var serializer = AvroSharpGeneric.CreateSerializer(registry, union);
        var line = new Generic.GenericRecord((Schemas.RecordSchema)((Schemas.UnionSchema)union).Branches[1]) { ["sku"] = "SKU-1", ["quantity"] = 2 };
        var lineMessage = await serializer.SerializeAsync(Generic.AvroValue.FromRecord(line), Value(topic));
        var cart = new Generic.GenericRecord((Schemas.RecordSchema)((Schemas.UnionSchema)union).Branches[0]) { ["owner"] = "Ada", ["lines"] = Generic.AvroValue.FromArray([]) };
        var cartMessage = await serializer.SerializeAsync(Generic.AvroValue.FromRecord(cart), Value(topic));
        var resolver = new AvroSharpMessageTypeResolver(registry, [typeof(Cart), typeof(CartLine)]);

        await Assert.That(await resolver.OnConsumeAsync(new ConsumedMessage(lineMessage!))).IsEqualTo(typeof(CartLine));
        await Assert.That(await resolver.OnConsumeAsync(new ConsumedMessage(cartMessage!))).IsEqualTo(typeof(Cart));
        var read = (CartLine)await new AvroSharpKafkaFlowDeserializer(registry).DeserializeAsync(new MemoryStream(lineMessage!), typeof(CartLine), new SerializerContext(topic));
        await Assert.That(read.Sku).IsEqualTo("SKU-1");
    }

    [Test]
    public async Task Resolver_MatchesAliases_AndRejectsOtherSchemaTypes()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var old = Schemas.AvroSchema.Parse("""{"type":"record","name":"OldShipment","namespace":"test.shop","fields":[{"name":"Carrier","type":"string"}]}""");
        var oldMessage = await AvroSharpGeneric.CreateSerializer(registry, old).SerializeAsync(
            Generic.AvroValue.FromRecord(new Generic.GenericRecord((Schemas.RecordSchema)old) { ["Carrier"] = "DHL" }), Value(topic));
        var protobuf = await registry.RegisterSchemaAsync(Topic() + "-value", new Schema("syntax = \"proto3\"; message M { int32 a = 1; }", SchemaType.Protobuf));
        var resolver = new AvroSharpMessageTypeResolver(registry, [typeof(Shipment)]);

        await Assert.That(await resolver.OnConsumeAsync(new ConsumedMessage(oldMessage!))).IsEqualTo(typeof(Shipment));
        await Assert.That(async () => await resolver.OnConsumeAsync(new ConsumedMessage([0, (byte)(protobuf >> 24), (byte)(protobuf >> 16), (byte)(protobuf >> 8), (byte)protobuf, 0])))
            .Throws<InvalidOperationException>().WithMessageContaining("not Avro", StringComparison.Ordinal);
        await Assert.That(() => new AvroSharpMessageTypeResolver(registry, [typeof(Shipment), typeof(OldShipmentClash)]))
            .Throws<ArgumentException>().WithMessageContaining("test.shop.OldShipment", StringComparison.Ordinal);
    }

    [Test]
    public async Task UnknownType_NamesTheRegistrationCall()
    {
        await Assert.That(() => new ConsumerMiddlewares().AddSchemaRegistryAvroSharpDeserializer<Outer.Inner>())
            .Throws<InvalidOperationException>().WithMessageContaining("AvroTypes.Register(AvroSharp.KafkaFlow.Tests.Outer.Inner.AvroTypeInfo)", StringComparison.Ordinal);
    }

    [Test]
    public async Task Serializer_RejectsWhatKafkaFlowCantCarry()
    {
        using var registry = new InMemorySchemaRegistry();

        await Assert.That(() => new AvroSharpKafkaFlowSerializer(registry, new AvroSharpSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header })).Throws<ArgumentException>();
        await Assert.That(async () => await Serialize(new AvroSharpKafkaFlowSerializer(registry), new Uri("https://example.com"), Topic())).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Extensions_CheckTheirArguments_WhenCalled()
    {
        var consumer = new ConsumerMiddlewares();
        var producer = new ProducerMiddlewares();

        await Assert.That(() => consumer.AddSchemaRegistryAvroSharpDeserializer<Uri>()).Throws<InvalidOperationException>();
        await Assert.That(() => consumer.AddSchemaRegistryAvroSharpDeserializer([typeof(Order), typeof(string)])).Throws<ArgumentException>();
        await Assert.That(() => producer.AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header })).Throws<ArgumentException>();
        await Assert.That(() => producer.AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { UseLatestVersion = true })).Throws<ArgumentException>();
        await Assert.That(consumer.Factories.Count + producer.Factories.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Extensions_WithoutASchemaRegistry_SaySo()
    {
        var consumer = new ConsumerMiddlewares()
            .AddSchemaRegistryAvroSharpDeserializer<Order>()
            .AddSchemaRegistryAvroSharpDeserializer([typeof(Order), typeof(Cart)]);
        var producer = new ProducerMiddlewares().AddSchemaRegistryAvroSharpSerializer();
        var noRegistry = new RegistryResolver(null);

        foreach (var factory in ((ConsumerMiddlewares)consumer).Factories.Concat(((ProducerMiddlewares)producer).Factories))
        {
            await Assert.That(() => factory(noRegistry)).Throws<InvalidOperationException>().WithMessageContaining("WithSchemaRegistry", StringComparison.Ordinal);
        }
    }

    [Test]
    public async Task Resolver_FindsATypeOfAnAssemblyNoCodeOfWhichRan()
    {
        using var registry = new InMemorySchemaRegistry();
        // Loaded for its metadata only, as a message handler's signature loads it: its module initializer hasn't run.
        System.Reflection.Assembly.Load("AvroSharp.Generators.LateTypes");
        var id = await registry.RegisterSchemaAsync(Topic() + "-value", new Schema(
            """{"type":"record","name":"LateEvent","namespace":"AvroSharp.Generators.LateTypes","fields":[]}""", SchemaType.Avro));
#pragma warning disable IL2026 // The test types are in assemblies that aren't trimmed.
        var found = new AvroSharpMessageTypeResolver(registry);
#pragma warning restore IL2026

        var type = await found.OnConsumeAsync(new ConsumedMessage([0, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]));

        await Assert.That(type.FullName).IsEqualTo("AvroSharp.Generators.LateTypes.LateEvent");
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

/// <summary>A record renamed from <c>OldShipment</c>, which it keeps as an alias.</summary>
[AvroSharp.Serialization.AvroSerializable(Namespace = "test.shop")]
[AvroSharp.Serialization.AvroAlias("test.shop.OldShipment")]
public partial class Shipment
{
    /// <summary>Gets or sets the carrier.</summary>
    public string Carrier { get; set; } = "";
}

/// <summary>A record whose own name is another type's alias.</summary>
[AvroSharp.Serialization.AvroSerializable(Namespace = "test.shop", Name = "OldShipment")]
public partial class OldShipmentClash
{
    /// <summary>Gets or sets the carrier.</summary>
    public string Carrier { get; set; } = "";
}

/// <summary>Holds a nested type, for the name in error messages.</summary>
internal static class Outer
{
    /// <summary>Not a type AvroSharp knows.</summary>
    internal enum Inner
    {
        /// <summary>A value.</summary>
        None,
    }
}
