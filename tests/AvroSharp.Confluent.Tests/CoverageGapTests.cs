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

/// <summary>Settings and paths the rc.2 review found untested (#215).</summary>
public class CoverageGapTests
{
    private const string PointV1 = """{"type":"record","name":"Point","namespace":"test.shop","fields":[{"name":"a","type":"int"}]}""";
    private const string PointV2 = """{"type":"record","name":"Point","namespace":"test.shop","fields":[{"name":"a","type":"int"},{"name":"b","type":"string","default":"x"}]}""";

    [Test]
    public async Task UseLatestWithMetadata_WritesThatVersionsId_AndChecksIt()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var tagged = await registry.RegisterSchemaAsync(topic + "-value", WithMetadata(AvroTypes.Get<Order>().Schema.ToJson(), "app", "billing"));
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        var otherTopic = Topic();
        await registry.RegisterSchemaAsync(otherTopic + "-value", WithMetadata(OrderV1, "app", "other"));

        var billing = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestWithMetadata = Metadata("app", "billing"), AutoRegisterSchemas = false });
        var bytes = await billing.SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(SchemaIdOf(bytes!)).IsEqualTo(tagged);
        var other = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseLatestWithMetadata = Metadata("app", "other"), AutoRegisterSchemas = false });
        // The version with that metadata has another schema, which the type's values don't encode as.
        await Assert.That(async () => await other.SerializeAsync(NewOrder(), Value(otherTopic))).Throws<InvalidOperationException>();
        await Assert.That(async () => await other.SerializeAsync(NewOrder(), Value(Topic()))).Throws<SchemaRegistryException>().Because("no version has that metadata");
    }

    [Test]
    public async Task Generic_UseLatestWithMetadata_ReadsAsThatVersion()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var v1 = (RecordSchema)AvroSchema.Parse(PointV1);
        var bytes = await AvroSharpGeneric.CreateSerializer(registry, v1).SerializeAsync(AvroValue.FromRecord(new GenericRecord(v1) { ["a"] = 1 }), Value(topic));
        await registry.RegisterSchemaAsync(topic + "-value", WithMetadata(PointV2, "app", "billing"));

        var read = (await AvroSharpGeneric.CreateDeserializer(registry, config: new AvroSharpDeserializerConfig { UseLatestWithMetadata = Metadata("app", "billing") })
            .DeserializeAsync(bytes, isNull: false, Value(topic))).AsRecord();

        await Assert.That(read["b"].AsString()).IsEqualTo("x");
    }

    [Test]
    public async Task NormalizeSchemas_ReachesTheRegistry()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { NormalizeSchemas = true }).SerializeAsync(NewOrder(), Value(topic));
        await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { NormalizeSchemas = true, AutoRegisterSchemas = false }).SerializeAsync(NewOrder(), Value(topic));
        await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(Topic()));

        await Assert.That(registry.NormalizeFlags).IsEquivalentTo([true, true, false], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SchemaIdInAHeader_ReadsWithDual_NotWithPrefix()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var headers = new Headers();
        var context = new SerializationContext(MessageComponentType.Value, topic, headers);
        var header = new AvroSharpSerializerConfig { SchemaIdStrategy = SchemaIdSerializerStrategy.Header };
        var bytes = await new AvroSharpSerializer<Order>(registry, header).SerializeAsync(NewOrder(), context);
        var raw = await new AvroSharpSerializer<byte[]>(registry, header).SerializeAsync([1, 2, 3], new SerializationContext(MessageComponentType.Value, Topic(), new Headers()));

        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { SchemaIdStrategy = SchemaIdDeserializerStrategy.Dual }).DeserializeAsync(bytes, isNull: false, context));
        await Assert.That(async () => await new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { SchemaIdStrategy = SchemaIdDeserializerStrategy.Prefix }).DeserializeAsync(bytes, isNull: false, context)).ThrowsException();
        // A bytes value with the ID in a header: the message body is the bytes alone.
        await Assert.That(raw).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task References_OverSeveralRounds_AndAnUndefinedName()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        const string Line = """{"type":"record","name":"Line","namespace":"test.shop","fields":[{"name":"sku","type":"string"}]}""";
        const string Box = """{"type":"record","name":"Box","namespace":"test.shop","fields":[{"name":"line","type":"test.shop.Line"}]}""";
        const string Shelf = """{"type":"record","name":"Shelf","namespace":"test.shop","fields":[{"name":"box","type":"test.shop.Box"},{"name":"line","type":"test.shop.Line"}]}""";
        await registry.RegisterSchemaAsync("test.shop.Line", new ConfluentSchema(Line, SchemaType.Avro));
        await registry.RegisterSchemaAsync("test.shop.Box", new ConfluentSchema(Box, [new SchemaReference("test.shop.Line", "test.shop.Line", 1)], SchemaType.Avro));
        // Box before Line: Box can't be parsed until Line is, so it takes a second round.
        var shelf = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(Shelf,
            [new SchemaReference("test.shop.Box", "test.shop.Box", 1), new SchemaReference("test.shop.Line", "test.shop.Line", 1)], SchemaType.Avro));
        var missing = await registry.RegisterSchemaAsync(Topic() + "-value", new ConfluentSchema(
            """{"type":"record","name":"Lost","namespace":"test.shop","fields":[{"name":"x","type":"test.shop.Missing"}]}""", SchemaType.Avro));
        byte[] message = [0, (byte)(shelf >> 24), (byte)(shelf >> 16), (byte)(shelf >> 8), (byte)shelf, 2, (byte)'a', 2, (byte)'b'];
        byte[] lost = [0, (byte)(missing >> 24), (byte)(missing >> 16), (byte)(missing >> 8), (byte)missing, 0];

        var read = (await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(message, isNull: false, Value(topic))).AsRecord();

        await Assert.That(read["box"].AsRecord()["line"].AsRecord()["sku"].AsString()).IsEqualTo("a");
        await Assert.That(read["line"].AsRecord()["sku"].AsString()).IsEqualTo("b");
        await Assert.That(async () => await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(lost, isNull: false, Value(Topic())))
            .Throws<AvroSchemaException>().WithMessageContaining("test.shop.Missing", StringComparison.Ordinal);
    }

    [Test]
    public async Task TopicRecord_MatchesConfluent_AndKeysReadUnderRecord()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var theirs = await new AvroSerializer<Order>(registry, new AvroSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord }).SerializeAsync(NewOrder(), Value(topic));
        var ours = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord }).SerializeAsync(NewOrder(), Value(topic));
        var key = new SerializationContext(MessageComponentType.Key, Topic());
        var keyBytes = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record }).SerializeAsync(NewOrder(), key);

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(await registry.GetAllSubjectsAsync()).Contains($"{topic}-test.shop.Order");
        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { SubjectNameStrategy = SubjectNameStrategy.Record }).DeserializeAsync(keyBytes, isNull: false, key));
    }

    [Test]
    public async Task SmallBuffers_AndASmallSchemaCache_StillRoundTrip()
    {
        using var registry = new InMemorySchemaRegistry { MaxCachedSchemas = 1 };
        var topic = Topic();
        var small = new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { BufferBytes = 1 });
        var v1 = (RecordSchema)AvroSchema.Parse(PointV1);
        var v2 = (RecordSchema)AvroSchema.Parse(PointV2);
        var first = await AvroSharpGeneric.CreateSerializer(registry, v1).SerializeAsync(AvroValue.FromRecord(new GenericRecord(v1) { ["a"] = 1 }), Value(Topic()));
        var second = await AvroSharpGeneric.CreateSerializer(registry, v2).SerializeAsync(AvroValue.FromRecord(new GenericRecord(v2) { ["a"] = 2, ["b"] = "y" }), Value(Topic()));
        var reader = AvroSharpGeneric.CreateDeserializer(registry);

        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(await small.SerializeAsync(NewOrder(), Value(topic)), isNull: false, Value(topic)));
        foreach (var round in Enumerable.Range(0, 3))
        {
            await Assert.That((await reader.DeserializeAsync(first, isNull: false, Value(topic))).AsRecord()["a"].AsInt32()).IsEqualTo(1).Because($"round {round}");
            await Assert.That((await reader.DeserializeAsync(second, isNull: false, Value(topic))).AsRecord()["b"].AsString()).IsEqualTo("y").Because($"round {round}");
        }
    }

    private static Dictionary<string, string> Metadata(string key, string value) => new(StringComparer.Ordinal) { [key] = value };

    private static ConfluentSchema WithMetadata(string schema, string key, string value) =>
        new(schema, [], SchemaType.Avro, new global::Confluent.SchemaRegistry.Metadata(null!, Metadata(key, value), null!), null);

    private static int SchemaIdOf(byte[] message) => (message[1] << 24) | (message[2] << 16) | (message[3] << 8) | message[4];
}
