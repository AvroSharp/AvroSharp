using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// Writing with the subject's latest schema, or the schema of <c>use.schema.id</c>, when it isn't the type's own
/// (#188): the value is written in that schema, after a compatibility check, as Java's and Confluent's generic
/// serializers write it.
/// </summary>
public class TargetSchemaTests
{
    private static readonly AvroSharpSerializerConfig Latest = new() { UseLatestVersion = true, AutoRegisterSchemas = false };

    [Test]
    public async Task ALatestSchemaWithANewField_IsWrittenWithIt_AsConfluentsGenericSerializer()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var latestId = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderWithChannel(), SchemaType.Avro));

        var ours = await new AvroSharpSerializer<Order>(registry, Latest).SerializeAsync(NewOrder(), Value(topic));
        using var other = new InMemorySchemaRegistry();
        var own = (await new AvroSharpSerializer<Order>(other).SerializeAsync(NewOrder(), Value(Topic())))!;

        // The type's encoding, then the new field's default: a record is its fields' encodings in order.
        await Assert.That(SchemaIdOf(ours!)).IsEqualTo(latestId);
        await Assert.That(ours!.Skip(5).ToArray()).IsEquivalentTo(own.Skip(5).Concat(new byte[] { 6, (byte)'w', (byte)'e', (byte)'b' }), CollectionOrdering.Matching);

        // Confluent's generic serde reads it as the latest schema, and writes the same bytes.
        var generic = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(ours, isNull: false, Value(topic));
        await Assert.That(generic["channel"]).IsEqualTo("web");
        var theirs = await new AvroSerializer<ApacheGenericRecord>(registry, new AvroSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false }).SerializeAsync(generic, Value(topic));
        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);

        // And the type reads it back, as every other message of the subject.
        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(ours, isNull: false, Value(topic)));
    }

    [Test]
    public async Task AnOlderLatestSchema_DropsTheFieldsItLacks()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));
        var serializer = new AvroSharpSerializer<Order>(registry, Latest);

        var bytes = await serializer.SerializeAsync(NewOrder(), Value(topic));
        var sync = serializer.Serialize(NewOrder(), Value(topic));

        var generic = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        await Assert.That(generic.Schema.Fields.Count).IsEqualTo(5);
        await Assert.That(generic["customer"]).IsEqualTo("Ada");
        var order = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        await Assert.That(order.Items.Count).IsEqualTo(2);
        await Assert.That(order.Status).IsEqualTo(Status.NEW);
        await Assert.That(order.Note).IsNull();
        await Assert.That(sync).IsEquivalentTo(bytes!, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnIncompatibleLatestSchema_IsRejected_NamingTheReason()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderWithChannel(withDefault: false), SchemaType.Avro));

        await Assert.That(async () => await new AvroSharpSerializer<Order>(registry, Latest).SerializeAsync(NewOrder(), Value(topic)))
            .Throws<InvalidOperationException>().WithMessageContaining("channel", StringComparison.Ordinal);
        await Assert.That(async () => await new AvroSharpSerializer<Order>(registry, Latest).SerializeAsync(NewOrder(), Value(topic)))
            .Throws<InvalidOperationException>().WithMessageContaining("latest.compatibility.strict", StringComparison.Ordinal);

        // Off, the schemas must still resolve, and these don't: the new field has nothing to be read from.
        var loose = new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false, LatestCompatibilityStrict = false };
        await Assert.That(async () => await new AvroSharpSerializer<Order>(registry, loose).SerializeAsync(NewOrder(), Value(topic))).Throws<AvroSchemaException>();
    }

    [Test]
    public async Task StrictOff_WritesTheValuesTheTargetCanHold()
    {
        // The latest schema reads the writer's union only when it holds null: compatible in part.
        var ours = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"N","namespace":"test.shop","fields":[{"name":"a","type":"int"},{"name":"n","type":["null","string"]}]}""");
        const string Target = """{"type":"record","name":"N","namespace":"test.shop","fields":[{"name":"a","type":"int"},{"name":"n","type":"null"}]}""";
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var id = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(Target, SchemaType.Avro));
        var withNull = AvroValue.FromRecord(new GenericRecord(ours) { ["a"] = 3, ["n"] = AvroValue.Null });
        var withText = AvroValue.FromRecord(new GenericRecord(ours) { ["a"] = 3, ["n"] = "x" });

        await Assert.That(async () => await AvroSharpGeneric.CreateSerializer(registry, ours, Latest).SerializeAsync(withNull, Value(topic))).Throws<InvalidOperationException>();
        var loose = AvroSharpGeneric.CreateSerializer(registry, ours, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false, LatestCompatibilityStrict = false });
        var bytes = await loose.SerializeAsync(withNull, Value(topic));
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 6 }, CollectionOrdering.Matching);
        await Assert.That(async () => await loose.SerializeAsync(withText, Value(topic))).Throws<Exception>();

        // use.schema.id has its own key, id.compatibility.strict.
        await Assert.That(async () => await AvroSharpGeneric.CreateSerializer(registry, ours, new AvroSharpSerializerConfig { UseSchemaId = id, AutoRegisterSchemas = false }).SerializeAsync(withNull, Value(topic)))
            .Throws<InvalidOperationException>().WithMessageContaining("id.compatibility.strict", StringComparison.Ordinal);
        var byId = await AvroSharpGeneric.CreateSerializer(registry, ours, new AvroSharpSerializerConfig { UseSchemaId = id, AutoRegisterSchemas = false, IdCompatibilityStrict = false }).SerializeAsync(withNull, Value(topic));
        await Assert.That(byId).IsEquivalentTo(bytes!, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UseSchemaId_OfAnOlderVersion_WritesIt()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var v1 = await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderV1, SchemaType.Avro));

        var bytes = await new AvroSharpSerializer<Order>(registry, new AvroSharpSerializerConfig { UseSchemaId = v1, AutoRegisterSchemas = false }).SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(SchemaIdOf(bytes!)).IsEqualTo(v1);
        var generic = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        await Assert.That(generic.Schema.Fields.Count).IsEqualTo(5);
    }

    [Test]
    public async Task TheTargetIsDecidedOncePerSchemaId()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(OrderWithChannel(), SchemaType.Avro));
        var serializer = new AvroSharpSerializer<Order>(registry, Latest);
        var first = await serializer.SerializeAsync(NewOrder(), Value(topic));
        var calls = registry.GetSchemaCalls;

        for (var i = 0; i < 5; i++)
        {
            await Assert.That(await serializer.SerializeAsync(NewOrder(), Value(topic))).IsEquivalentTo(first!, CollectionOrdering.Matching);
        }

        await Assert.That(registry.GetSchemaCalls).IsEqualTo(calls);
    }

    // The type's schema with a string field "channel" at the end, with the default "web" or none.
    private static string OrderWithChannel(bool withDefault = true)
    {
        var json = JsonNode.Parse(AvroTypes.Get<Order>().Schema.ToJson())!.AsObject();
        var field = new JsonObject { ["name"] = "channel", ["type"] = "string" };
        if (withDefault)
        {
            field["default"] = "web";
        }

        json["fields"]!.AsArray().Add(field);
        return json.ToJsonString();
    }

    private static int SchemaIdOf(byte[] message) => (message[1] << 24) | (message[2] << 16) | (message[3] << 8) | message[4];
}
