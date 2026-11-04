using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using static AvroSharp.Confluent.Tests.TestData;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// Subjects named by a function, and schemas pinned by subject and version or by GUID (#194). A pinned schema that
/// differs from the type's is written as #188 writes it.
/// </summary>
public class PinnedSchemaTests
{
    [Test]
    public async Task CustomSubjectNameStrategy_NamesKeyAndValueSubjects()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var asked = new ConcurrentBag<string>();
        Task<string> Billing(SerializationContext context, string recordType)
        {
            var subject = $":.billing:{context.Topic}-{(context.Component == MessageComponentType.Key ? "key" : "value")}";
            asked.Add(subject);
            return Task.FromResult(subject);
        }

        var keys = new AvroSharpSerializer<string>(registry) { CustomSubjectNameStrategy = Billing };
        var values = new AvroSharpSerializer<Order>(registry) { CustomSubjectNameStrategy = Billing };
        await keys.SerializeAsync("k-1", Key(topic));
        var message = values.Serialize(NewOrder(), Value(topic));

        await Assert.That(await registry.GetAllSubjectsAsync()).Contains($":.billing:{topic}-key");
        await Assert.That(await registry.GetAllSubjectsAsync()).Contains($":.billing:{topic}-value");
        await Assert.That(await registry.GetAllSubjectsAsync()).DoesNotContain($"{topic}-value");

        // The deserializer asks for the subject when it needs the latest schema.
        asked.Clear();
        var reader = new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { UseLatestVersion = true }) { CustomSubjectNameStrategy = Billing };
        await ConfluentInteropTests.AssertIsNewOrder(await reader.DeserializeAsync(message, isNull: false, Value(topic)));
        await Assert.That(asked).Contains($":.billing:{topic}-value");
        await Assert.That(() => new AvroSharpSerializer<Order>(registry) { CustomSubjectNameStrategy = null! }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PinnedBySubjectAndVersion_WritesThatVersionsId_ForEveryTopic()
    {
        using var registry = new InMemorySchemaRegistry();
        var subject = Topic() + "-value";
        var v1 = await registry.RegisterSchemaAsync(subject, new ConfluentSchema(OrderV1, SchemaType.Avro));
        var v2 = await registry.RegisterSchemaAsync(subject, new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        var keySubject = Topic() + "-key";
        var key = await registry.RegisterSchemaAsync(keySubject, new ConfluentSchema("\"string\"", SchemaType.Avro));

        var first = await new InspectedProducerBuilder().SetAvroSharpValueSerializerAsync(registry, subject, 1);
        var second = await new InspectedProducerBuilder().SetAvroSharpKeySerializerAsync(registry, keySubject, 1);
        var latest = await new InspectedProducerBuilder().SetAvroSharpValueSerializerAsync(registry, subject, 2);
        var versionOne = (ISerializer<Order>)((InspectedProducerBuilder)first).Value!;

        var bytes = versionOne.Serialize(NewOrder(), Value(Topic()));
        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(v1);
        await Assert.That(SchemaIdOf(versionOne.Serialize(NewOrder(), Value(Topic())))).IsEqualTo(v1);
        await Assert.That(SchemaIdOf(((ISerializer<Order>)((InspectedProducerBuilder)latest).Value!).Serialize(NewOrder(), Value(Topic())))).IsEqualTo(v2);
        await Assert.That(SchemaIdOf(((ISerializer<string>)((InspectedProducerBuilder)second).Key!).Serialize("k", Key(Topic())))).IsEqualTo(key);

        // Version 1 lacks some of the type's fields, so the value is written in its schema (#188).
        var generic = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(bytes, isNull: false, Value(Topic()));
        await Assert.That(generic.Schema.Fields.Count).IsEqualTo(5);
        // Nothing was registered.
        await Assert.That(await registry.GetSubjectVersionsAsync(subject)).Count().IsEqualTo(2);
        await Assert.That(await registry.GetSubjectVersionsAsync(keySubject)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task UseSchemaGuid_WritesThatSchemasVersionOfTheSubject()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var v1 = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(AvroTypes.Get<Order>().Schema.ToJson(), SchemaType.Avro));
        var guid = (await registry.GetRegisteredSchemaAsync(topic + "-value", 1)).Guid;
        var serializer = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaGuid = guid, AutoRegisterSchemas = false });

        var bytes = await serializer.SerializeAsync(NewOrder(), Value(topic));
        var again = serializer.Serialize(NewOrder(), Value(topic));

        await Assert.That(SchemaIdOf(bytes)).IsEqualTo(v1);
        await Assert.That(SchemaIdOf(again)).IsEqualTo(v1);
        var generic = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        await Assert.That(generic.Schema.Fields.Count).IsEqualTo(5);
    }

    [Test]
    public async Task UseSchemaGuid_IsCheckedAsAPinnedId_AndCantBeCombined()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        // A version the type can't be written in: a field with no default.
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(
            """{"type":"record","name":"Order","namespace":"test.shop","fields":[{"name":"customer","type":"string"},{"name":"region","type":"string"}]}""", SchemaType.Avro));
        var guid = (await registry.GetRegisteredSchemaAsync(topic + "-value", 1)).Guid;

        await Assert.That(async () => await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaGuid = guid, AutoRegisterSchemas = false }).SerializeAsync(NewOrder(), Value(topic)))
            .Throws<InvalidOperationException>().WithMessageContaining("id.compatibility.strict", StringComparison.Ordinal);
        await Assert.That(() => new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaGuid = guid, UseSchemaId = 1 })).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaGuid = guid, UseLatestVersion = true, AutoRegisterSchemas = false })).Throws<ArgumentException>();
        await Assert.That(new AvroSharpSerializerConfig([new("avro.serializer.use.schema.guid", guid)]).UseSchemaGuid).IsEqualTo(guid);
    }

    private static int SchemaIdOf(byte[]? message) => (message![1] << 24) | (message[2] << 16) | (message[3] << 8) | message[4];

    // Not built: that loads librdkafka, which has no Windows Arm64 build.
    private sealed class InspectedProducerBuilder() : ProducerBuilder<string, Order>(new ProducerConfig())
    {
        public object? Key => KeySerializer;

        public object? Value => ValueSerializer;
    }
}
