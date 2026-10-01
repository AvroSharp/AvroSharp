using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>The settings, registration, references and generic values.</summary>
public class SerdeBehaviorTests
{
    private const string CartLineSchema = """{"type":"record","name":"CartLine","namespace":"test.shop","fields":[{"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}""";
    private const string CartWithReference = """{"type":"record","name":"Cart","namespace":"test.shop","fields":[{"name":"owner","type":"string"},{"name":"lines","type":{"type":"array","items":"test.shop.CartLine"}}]}""";

    [Test]
    public async Task Serializer_RegistersOncePerSubject()
    {
        using var registry = new InMemorySchemaRegistry();
        var serializer = new AvroSharpSerializer<Order>(registry);
        var topic = Topic();

        for (var i = 0; i < 3; i++)
        {
            await serializer.SerializeAsync(NewOrder(), Value(topic));
        }

        await serializer.SerializeAsync(NewOrder(), Value(Topic()));

        await Assert.That(registry.RegistrationCalls).IsEqualTo(2);
    }

    [Test]
    public async Task WithoutAutoRegistration_TheSchemaIsLookedUp()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { AutoRegisterSchemas = false });

        await Assert.That(async () => await serializer.SerializeAsync(NewOrder(), Value(topic))).Throws<SchemaRegistryException>();

        var id = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        var bytes = await serializer.SerializeAsync(NewOrder(), Value(topic));
        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(id);
    }

    [Test]
    public async Task UseLatestVersion_WritesWithTheLatestId()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        var latest = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        var config = new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false };

        var bytes = await new AvroSharpSerializer<Order>(registry, config).SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(latest);
        await Assert.That(registry.RegistrationCalls).IsEqualTo(2);
    }

    [Test]
    public async Task UseSchemaId_WritesWithThatId()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var id = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));

        var bytes = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaId = id, AutoRegisterSchemas = false }).SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(id);
    }

    [Test]
    public async Task UseLatestVersion_WithAutoRegistration_IsRejected()
    {
        using var registry = new InMemorySchemaRegistry();

        await Assert.That(() => new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestVersion = true })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Config_TakesConfluentsKeys()
    {
        var serializer = new AvroSharpSerializerConfig(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["avro.serializer.auto.register.schemas"] = "false",
            ["avro.serializer.use.latest.version"] = "true",
            ["avro.serializer.subject.name.strategy"] = "Record",
            ["avro.serializer.buffer.bytes"] = "512",
        });
        var deserializer = new AvroSharpDeserializerConfig(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["avro.deserializer.subject.name.strategy"] = "TopicRecord",
        });

        await Assert.That(serializer.AutoRegisterSchemas).IsFalse();
        await Assert.That(serializer.UseLatestVersion).IsTrue();
        await Assert.That(serializer.SubjectNameStrategy).IsEqualTo(SubjectNameStrategy.Record);
        await Assert.That(serializer.BufferBytes).IsEqualTo(512);
        await Assert.That(deserializer.SubjectNameStrategy).IsEqualTo(SubjectNameStrategy.TopicRecord);
        await Assert.That(AvroSharpSerializerConfig.PropertyNames.SchemaIdStrategy).IsEqualTo(AvroSerializerConfig.PropertyNames.SchemaIdStrategy);
        await Assert.That(AvroSharpDeserializerConfig.PropertyNames.UseLatestWithMetadata).IsEqualTo(AvroDeserializerConfig.PropertyNames.UseLatestWithMetadata);
    }

    [Test]
    public async Task HeaderSchemaId_MatchesConfluent()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirHeaders = new Headers();
        var ourHeaders = new Headers();

        var theirs = await new AvroSerializer<Order>(registry, new AvroSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header }).SerializeAsync(NewOrder(), new SerializationContext(MessageComponentType.Value, topic, theirHeaders));
        var ours = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header }).SerializeAsync(NewOrder(), new SerializationContext(MessageComponentType.Value, topic, ourHeaders));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(ourHeaders.Select(h => (h.Key, Convert.ToHexString(h.GetValueBytes())))).IsEquivalentTo(theirHeaders.Select(h => (h.Key, Convert.ToHexString(h.GetValueBytes()))), CollectionOrdering.Matching);
        var back = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(theirs, isNull: false, new SerializationContext(MessageComponentType.Value, topic, theirHeaders));
        await ConfluentInteropTests.AssertIsNewOrder(back);
    }

    [Test]
    public async Task SchemaWithReferences_IsReadAndWritten()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync("test.shop.CartLine", new ConfluentSchema(CartLineSchema, SchemaType.Avro));
        var cartId = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(CartWithReference, [new SchemaReference("test.shop.CartLine", "test.shop.CartLine", 1)], SchemaType.Avro));
        var cart = new Cart { Owner = "Ada", Lines = new List<CartLine> { new() { Sku = "SKU-1", Quantity = 3 } } };

        var bytes = await new AvroSharpSerializer<Cart>(registry, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false }).SerializeAsync(cart, Value(topic));
        var ours = await new AvroSharpDeserializer<Cart>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        var theirs = await new AvroDeserializer<Cart>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        var generic = await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(cartId);
        await Assert.That(ours.Lines.Single().Quantity).IsEqualTo(3);
        await Assert.That(theirs.Lines.Single().Sku).IsEqualTo("SKU-1");
        await Assert.That(generic.AsRecord()["lines"].AsArray().Single().AsRecord()["sku"].AsString()).IsEqualTo("SKU-1");
    }

    [Test]
    public async Task Generic_RoundTripsConfluentsBytes()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirs = await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        var value = await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(theirs, isNull: false, Value(topic));
        var record = value.AsRecord();
        var ours = await AvroSharpGeneric.CreateSerializer(registry, record.Schema).SerializeAsync(value, Value(topic));

        await Assert.That(record["customer"].AsString()).IsEqualTo("Ada");
        await Assert.That(record["status"].AsEnumSymbol()).IsEqualTo("PAID");
        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Generic_Bytes_AreTheMessageBody()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirs = await new AvroSerializer<byte[]>(registry).SerializeAsync([1, 2, 3], Value(topic));

        var ours = await AvroSharpGeneric.CreateSerializer(registry, AvroSchema.Bytes).SerializeAsync(AvroValue.FromBytes([1, 2, 3]), Value(topic));
        var back = await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(theirs, isNull: false, Value(topic));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(back.AsBytes()).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UseLatestVersion_WithAnotherSchema_IsRejected()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false });

        await Assert.That(async () => await serializer.SerializeAsync(NewOrder(), Value(topic))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UseSchemaId_OfAnotherSchema_IsRejected()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var v1 = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaId = v1, AutoRegisterSchemas = false });

        await Assert.That(async () => await serializer.SerializeAsync(NewOrder(), Value(topic))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UseLatestVersion_WithTheSameSchemaWrittenDifferently_IsAccepted()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        // Apache.Avro's JSON for the schema: other key order and repeated namespaces, the same canonical form.
        await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false });

        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(await serializer.SerializeAsync(NewOrder(), Value(topic)), isNull: false, Value(topic)));
    }

    [Test]
    public async Task GenericNull_IsATombstone()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var record = AvroSharpGeneric.CreateSerializer(registry, AvroTypes.Get<Order>().Schema);
        var nullSchema = AvroSharpGeneric.CreateSerializer(registry, AvroSchema.Null);

        await Assert.That(await record.SerializeAsync(default, Value(topic))).IsNull();
        // A value of the schema "null" is a message: the schema ID and no Avro bytes.
        await Assert.That(await nullSchema.SerializeAsync(default, Value(Topic()))).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Tombstone_ReadsAsNull_ExceptIntoAValueType()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var order = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic));
        var generic = await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic));

        await Assert.That(order).IsNull();
        await Assert.That(generic.IsNull).IsTrue();
        await Assert.That(async () => await new AvroSharpDeserializer<int>(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Config_RejectsUnknownKeys()
    {
        using var registry = new InMemorySchemaRegistry();
        var misspelled = new AvroSharpSerializerConfig(new Dictionary<string, string>(StringComparer.Ordinal) { ["avro.serializer.auto.register.schema"] = "false" });
        var deserializerKey = new AvroSharpSerializerConfig(new Dictionary<string, string>(StringComparer.Ordinal) { ["avro.deserializer.use.latest.version"] = "true" });
        var accepted = new AvroSharpSerializerConfig(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["avro.serializer.normalize.schemas"] = "true",
            ["rules.secret"] = "s",
            ["subject.name.strategy.kafka.cluster.id"] = "c",
        });
        var deserializer = new AvroSharpDeserializerConfig(new Dictionary<string, string>(StringComparer.Ordinal) { ["avro.deserializer.schema.id.strategy"] = "Prefix" });

        await Assert.That(() => new AvroSharpSerializer<Order>(registry, misspelled)).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpSerializer<Order>(registry, deserializerKey)).Throws<ArgumentException>();
        await Assert.That(new AvroSharpSerializer<Order>(registry, accepted)).IsNotNull();
        await Assert.That(new AvroSharpDeserializer<Order>(registry, deserializer)).IsNotNull();
    }

    [Test]
    public async Task RecordStrategy_ForAPrimitive_SaysWhy()
    {
        using var registry = new InMemorySchemaRegistry();
        var serializer = new AvroSharpSerializer<string>(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record });

        var thrown = await Assert.That(async () => await serializer.SerializeAsync("x", Value(Topic()))).Throws<InvalidOperationException>();

        await Assert.That(thrown!.Message).Contains("Record");
    }

    [Test]
    public async Task UnknownType_IsReported()
    {
        using var registry = new InMemorySchemaRegistry();

        await Assert.That(() => new AvroSharpSerializer<Uri>(registry)).Throws<InvalidOperationException>();
        await Assert.That(() => new AvroSharpDeserializer<Uri>(registry)).Throws<InvalidOperationException>();
    }

    private static int SchemaIdOf(byte[] message) => (message[1] << 24) | (message[2] << 16) | (message[3] << 8) | message[4];
}
