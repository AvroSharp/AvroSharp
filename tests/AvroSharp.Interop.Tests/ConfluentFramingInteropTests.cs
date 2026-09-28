using System;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Messages;
using Confluent.Kafka;
using Confluent.SchemaRegistry.Serdes;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Confluent framing (#81) checked against Confluent's own Avro serializer and deserializer (Confluent.SchemaRegistry.Serdes.Avro),
/// with an in-memory stand-in for the registry client that assigns one schema ID.
/// </summary>
public class ConfluentFramingInteropTests
{
    private const string SchemaJson =
        """{"type":"record","name":"User","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"tags","type":{"type":"array","items":"string"}}]}""";

    private const int SchemaId = 1234567;

    [Test]
    public async Task OurMessages_AreByteIdenticalToConfluentsSerializer_AndReadEachOther()
    {
        using var registry = FakeSchemaRegistryClient.Create(SchemaId, SchemaJson);
        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(SchemaJson);
        var apacheRecord = new ApacheGenericRecord(apacheSchema);
        apacheRecord.Add("id", 42L);
        apacheRecord.Add("name", "Ada");
        apacheRecord.Add("tags", new object[] { "a", "bb" });

        var context = new SerializationContext(MessageComponentType.Value, "users");
        var theirs = await new AvroSerializer<ApacheGenericRecord>(registry).SerializeAsync(apacheRecord, context);

        var schema = AvroSchema.Parse(SchemaJson);
        var ours = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(SchemaId),
            new GenericRecord((Schemas.RecordSchema)schema) { ["id"] = 42L, ["name"] = "Ada", ["tags"] = AvroValue.FromArray(new AvroValue[] { "a", "bb" }) },
            GenericDatumWriter.Create(schema));

        var store = new AvroSchemaIdStore();
        store.Add(AvroSchemaId.FromNumber(SchemaId), schema);
        var readByUs = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, store).Read(theirs).AsRecord();
        var readByThem = await new AvroDeserializer<ApacheGenericRecord>(registry).DeserializeAsync(ours, isNull: false, context);

        await Assert.That(Convert.ToHexString(ours)).IsEqualTo(Convert.ToHexString(theirs));
        await Assert.That(readByUs["name"].AsString()).IsEqualTo("Ada");
        await Assert.That(readByThem["id"]).IsEqualTo(42L);
    }
}
