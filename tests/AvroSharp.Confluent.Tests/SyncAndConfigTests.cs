using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Configuration;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>The synchronous serializer and deserializer, the configuration constructors, and the generic serializer of each record's own schema.</summary>
public class SyncAndConfigTests
{
    private const string PointSchema = """{"type":"record","name":"Point","namespace":"test.shop","fields":[{"name":"x","type":"int"}]}""";
    private const string LabelSchema = """{"type":"record","name":"Label","namespace":"test.shop","fields":[{"name":"text","type":"string"}]}""";

    [Test]
    public async Task Sync_WritesAndReadsTheBytesOfAsync()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpSerializer<Order>(registry);
        var deserializer = new AvroSharpDeserializer<Order>(registry);

        var first = serializer.Serialize(NewOrder(), Value(topic));
        var again = serializer.Serialize(NewOrder(), Value(topic));
        var async = await serializer.SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(first).IsEquivalentTo(async!, CollectionOrdering.Matching);
        await Assert.That(again).IsEquivalentTo(async!, CollectionOrdering.Matching);
        await Assert.That(registry.RegistrationCalls).IsEqualTo(1);
        await ConfluentInteropTests.AssertIsNewOrder(deserializer.Deserialize(first, isNull: false, Value(topic)));
        await ConfluentInteropTests.AssertIsNewOrder(deserializer.Deserialize(again, isNull: false, Value(topic)));
        await Assert.That(serializer.Serialize(null!, Value(topic))).IsNull();
        await Assert.That(deserializer.Deserialize(default, isNull: true, Value(topic))).IsNull();
    }

    // After warm-up the synchronous path allocates only the message (no task or state machine), so it allocates less
    // than the asynchronous one, which goes through Confluent's subject and latest-schema lookups for every message.
    [Test]
    public async Task Sync_AfterWarmUp_AllocatesLessThanAsync()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpSerializer<Order>(registry);
        var deserializer = new AvroSharpDeserializer<Order>(registry);
        var order = NewOrder();
        var message = serializer.Serialize(order, Value(topic))!;
        deserializer.Deserialize(message, isNull: false, Value(topic));
        await serializer.SerializeAsync(order, Value(topic));
        await deserializer.DeserializeAsync(message, isNull: false, Value(topic));

        var sync = Allocated(() => serializer.Serialize(order, Value(topic)));
        var async = Allocated(() => serializer.SerializeAsync(order, Value(topic)).GetAwaiter().GetResult());
        var syncRead = Allocated(() => deserializer.Deserialize(message, isNull: false, Value(topic)));
        var asyncRead = Allocated(() => deserializer.DeserializeAsync(message, isNull: false, Value(topic)).GetAwaiter().GetResult());

        await Assert.That(sync).IsLessThan(async);
        await Assert.That(syncRead).IsLessThan(asyncRead);

        static long Allocated(Action action)
        {
            const int Messages = 100;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Messages; i++)
            {
                action();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / Messages;
        }
    }

    [Test]
    public async Task Sync_ReadsConfluentsMessages_AndKeysApartFromValues()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirs = await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));
        var key = new AvroSharpSerializer<string>(registry).Serialize("order-1", new SerializationContext(MessageComponentType.Key, topic));

        await ConfluentInteropTests.AssertIsNewOrder(new AvroSharpDeserializer<Order>(registry).Deserialize(theirs, isNull: false, Value(topic)));
        await Assert.That(new AvroSharpDeserializer<string>(registry).Deserialize(key, isNull: false, new SerializationContext(MessageComponentType.Key, topic))).IsEqualTo("order-1");
        await Assert.That(await registry.GetAllSubjectsAsync()).IsEquivalentTo([$"{topic}-value", $"{topic}-key"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Sync_WithUseLatestVersion_StillChecksEachMessage()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false });

        var bytes = serializer.Serialize(NewOrder(), Value(topic));
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));

        await Assert.That(bytes).IsNotEmpty();
        // The latest version now has another schema, which the serializer sees on the next message.
        await Assert.That(() => serializer.Serialize(NewOrder(), Value(topic))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuilderExtensions_SetTheSynchronousInterfaces()
    {
        using var registry = new InMemorySchemaRegistry();
        var rules = new RuleRegistry();

        using var producer = new ProducerBuilder<string, Order>(new ProducerConfig { BootstrapServers = "127.0.0.1:1" })
            .SetAvroSharpKeySerializer(registry, ruleRegistry: rules)
            .SetAvroSharpValueSerializer(registry, ruleRegistry: rules)
            .Build();
        using var consumer = new ConsumerBuilder<string, Order>(new ConsumerConfig { BootstrapServers = "127.0.0.1:1", GroupId = "g" })
            .SetAvroSharpKeyDeserializer(registry, ruleRegistry: rules)
            .SetAvroSharpValueDeserializer(registry, ruleRegistry: rules)
            .Build();

        await Assert.That(producer.Name).IsNotEmpty();
        await Assert.That(consumer.Name).IsNotEmpty();
    }

    [Test]
    public async Task Config_CopiesThePairs()
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal) { ["avro.serializer.buffer.bytes"] = "512" };
        var readOnly = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal) { ["avro.deserializer.use.latest.version"] = "true" });

        var serializer = new AvroSharpSerializerConfig(pairs) { AutoRegisterSchemas = false };
        var deserializer = new AvroSharpDeserializerConfig(readOnly) { UseLatestVersion = false };

        await Assert.That(serializer.BufferBytes).IsEqualTo(512);
        await Assert.That(serializer.AutoRegisterSchemas).IsFalse();
        await Assert.That(pairs.Keys).IsEquivalentTo(["avro.serializer.buffer.bytes"], CollectionOrdering.Any);
        await Assert.That(deserializer.UseLatestVersion).IsFalse();
        await Assert.That(readOnly["avro.deserializer.use.latest.version"]).IsEqualTo("true");
    }

    [Test]
    public async Task Config_FromAnAppSettingsSection()
    {
        const string AppSettings = """
            {
              "Kafka": {
                "Serializer": { "avro.serializer.auto.register.schemas": "false", "avro.serializer.subject.name.strategy": "Record" },
                "Deserializer": { "avro.deserializer.use.latest.version": "true" }
              }
            }
            """;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(AppSettings))).Build();

        // A section's pairs, with keys relative to it. The section itself has no value, and is left out.
        var serializer = new AvroSharpSerializerConfig(configuration.GetSection("Kafka:Serializer").AsEnumerable(makePathsRelative: true)!);
        var deserializer = new AvroSharpDeserializerConfig(configuration.GetSection("Kafka:Deserializer").AsEnumerable(makePathsRelative: true)!);

        await Assert.That(serializer.AutoRegisterSchemas).IsFalse();
        await Assert.That(serializer.SubjectNameStrategy).IsEqualTo(SubjectNameStrategy.Record);
        await Assert.That(deserializer.UseLatestVersion).IsTrue();
        // Without auto-registration, a schema the subject doesn't have is looked up and not found.
        using var registry = new InMemorySchemaRegistry();
        await Assert.That(() => new AvroSharpSerializer<Order>(registry, serializer).Serialize(NewOrder(), Value(Topic()))).Throws<SchemaRegistryException>();
    }

    [Test]
    public async Task GenericRecordSerializer_WritesEachRecordWithItsSchema_AsConfluents()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var point = (RecordSchema)AvroSchema.Parse(PointSchema);
        var label = (RecordSchema)AvroSchema.Parse(LabelSchema);
        var config = new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord };
        var ours = AvroSharpGeneric.CreateSerializer(registry, config);

        var ourPoint = await ours.SerializeAsync(AvroValue.FromRecord(new GenericRecord(point) { ["x"] = 7 }), Value(topic));
        var ourLabel = ours.Serialize(AvroValue.FromRecord(new GenericRecord(label) { ["text"] = "hi" }), Value(topic));
        var theirs = new AvroSerializer<ApacheGenericRecord>(registry, new AvroSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });
        var apachePoint = new ApacheGenericRecord((Avro.RecordSchema)Avro.Schema.Parse(PointSchema));
        apachePoint.Add("x", 7);
        var apacheLabel = new ApacheGenericRecord((Avro.RecordSchema)Avro.Schema.Parse(LabelSchema));
        apacheLabel.Add("text", "hi");

        await Assert.That(ourPoint).IsEquivalentTo(await theirs.SerializeAsync(apachePoint, Value(topic)), CollectionOrdering.Matching);
        await Assert.That(ourLabel).IsEquivalentTo(await theirs.SerializeAsync(apacheLabel, Value(topic)), CollectionOrdering.Matching);
        await Assert.That(await registry.GetAllSubjectsAsync()).IsEquivalentTo([$"{topic}-test.shop.Point", $"{topic}-test.shop.Label"], CollectionOrdering.Any);
        await Assert.That(await ours.SerializeAsync(AvroValue.Null, Value(topic))).IsNull();
        await Assert.That(() => ours.Serialize(AvroValue.FromInt32(1), Value(topic))).Throws<ArgumentException>().WithMessageContaining("not a record", StringComparison.Ordinal);
        await Assert.That(() => AvroSharpGeneric.CreateSerializer(registry, new AvroSharpSerializerConfig { UseLatestVersion = true })).Throws<ArgumentException>();
    }
}
