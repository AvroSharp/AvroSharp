using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Glue;
using Avro.IO;
using Avro.Specific;
using AvroSharp.Generic;
using AvroSharp.Serialization;
using Confluent.Kafka;
using test.glue;
using TUnit.Assertions.Enums;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ApacheGenericWriter = Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>;
using GenericRecord = AvroSharp.Generic.GenericRecord;

namespace AvroSharp.Aws.Glue.Tests;

/// <summary>
/// AWS Glue Schema Registry's wire format and registration flow, against an in-memory Glue client. AWS's own .NET
/// serializer is a native build for Linux only, so the messages are compared with what its encoder writes, from AWS's
/// Java source (SerializationDataEncoder): <c>0x03</c>, the compression byte, the schema version UUID as two
/// big-endian longs (its most significant bits first), then Apache.Avro's encoding of the value.
/// </summary>
public class GlueSerializerTests
{
    private const string OrderV1 = """
        {"type":"record","name":"Order","namespace":"test.glue","fields":[
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"customer","type":"string"},
          {"name":"placed_at","type":{"type":"long","logicalType":"timestamp-millis"}},
          {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
          {"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[
            {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]}
        """;

    [Test]
    public async Task Message_IsAwsEncodersBytes()
    {
        using var glue = new InMemoryGlueClient();
        var versionId = glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());

        var message = await new AvroSharpGlueSerializer(glue).SerializeAsync(NewOrder(), "orders");

        await Assert.That(message).IsEquivalentTo(AwsMessage(versionId, compression: 0x00, ApacheBytes(NewOrder())), CollectionOrdering.Matching);
    }

    [Test]
    public async Task UuidByteOrder_IsMostSignificantBitsFirst()
    {
        // The UUID 00112233-4455-6677-8899-aabbccddeeff, as Java's ByteBuffer.putLong(msb).putLong(lsb) writes it.
        using var glue = new InMemoryGlueClient();
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson(), id: "00112233-4455-6677-8899-aabbccddeeff");

        var message = await new AvroSharpGlueSerializer(glue).SerializeAsync(NewOrder(), "orders");

        await Assert.That(message.Take(18)).IsEquivalentTo(
            new byte[] { 0x03, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff },
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadsAwsMessages_CompressedOrNot()
    {
        using var glue = new InMemoryGlueClient();
        var versionId = glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var plain = AwsMessage(versionId, compression: 0x00, ApacheBytes(NewOrder()));
        var compressed = AwsMessage(versionId, compression: 0x05, Zlib(ApacheBytes(NewOrder())));
        var serializer = new AvroSharpGlueSerializer(glue);

        await AssertIsNewOrder(await serializer.DeserializeAsync<Order>(plain));
        await AssertIsNewOrder(await serializer.DeserializeAsync<Order>(compressed));
        await Assert.That(AvroSharpGlueSerializer.CanDecode(compressed)).IsTrue();
        await Assert.That(AvroSharpGlueSerializer.CanDecode([0x00, 0x01, 0x02])).IsFalse();
    }

    [Test]
    public async Task Compression_WritesZlib_ThatDecompressesToTheAvroData()
    {
        using var glue = new InMemoryGlueClient();
        var versionId = glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { Compression = AvroSharpGlueCompression.Zlib });

        var message = await serializer.SerializeAsync(NewOrder(), "orders");

        await Assert.That(message.Take(18)).IsEquivalentTo(AwsMessage(versionId, compression: 0x05, []), CollectionOrdering.Matching);
        await Assert.That(Unzlib(message[18..])).IsEquivalentTo(ApacheBytes(NewOrder()), CollectionOrdering.Matching);
        await AssertIsNewOrder(await serializer.DeserializeAsync<Order>(message));
    }

    [Test]
    public async Task OlderWriter_ReadsAsTheCurrentType()
    {
        using var glue = new InMemoryGlueClient();
        var v1 = (Avro.RecordSchema)Avro.Schema.Parse(OrderV1);
        var versionId = glue.Add("orders", v1.ToString());
        var old = new ApacheGenericRecord(v1);
        old.Add("id", Guid.NewGuid());
        old.Add("customer", "from v1");
        old.Add("placed_at", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        old.Add("total", new Avro.AvroDecimal(5.00m));
        old.Add("items", Array.Empty<object>());

        var order = await new AvroSharpGlueSerializer(glue).DeserializeAsync<Order>(AwsMessage(versionId, 0x00, ApacheBytes(old, v1)));

        await Assert.That(order.Customer).IsEqualTo("from v1");
        await Assert.That(order.Status).IsEqualTo(Status.NEW);
        await Assert.That(order.Note).IsNull();
    }

    [Test]
    public async Task GenericRecords_AreWrittenAndReadInTheWritersSchema()
    {
        using var glue = new InMemoryGlueClient();
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true });
        var schema = (Schemas.RecordSchema)Schemas.AvroSchema.Parse(OrderV1);
        var record = new GenericRecord(schema)
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["customer"] = "generic",
            ["placed_at"] = 0L,
            ["total"] = new byte[] { 0x01, 0xF4 },
            ["items"] = AvroValue.FromArray([]),
        };

        var message = await serializer.SerializeAsync(record, "orders");
        var asRecord = await serializer.DeserializeAsync<GenericRecord>(message);
        var asValue = await serializer.DeserializeAsync<AvroValue>(message);
        var asOrder = await serializer.DeserializeAsync<Order>(message);

        await Assert.That(asRecord["customer"].AsString()).IsEqualTo("generic");
        await Assert.That(asValue.AsRecord().Schema.FullName).IsEqualTo("test.glue.Order");
        await Assert.That(asOrder.Total).IsEqualTo(new Avro.AvroDecimal(5.00m));
    }

    [Test]
    public async Task AutoRegistration_CreatesTheSchema_ThenRegistersNewVersions()
    {
        using var glue = new InMemoryGlueClient();
        var options = new AvroSharpGlueOptions
        {
            AutoRegisterSchemas = true,
            RegistryName = "shop",
            Compatibility = Compatibility.FULL,
            Description = "Orders",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["team"] = "billing" },
        };

        await new AvroSharpGlueSerializer(glue, options).SerializeAsync(OldOrder(), "orders");
        await new AvroSharpGlueSerializer(glue, options).SerializeAsync(NewOrder(), "orders");

        var created = glue.Created.Single();
        await Assert.That(created.SchemaName).IsEqualTo("orders");
        await Assert.That(created.RegistryId.RegistryName).IsEqualTo("shop");
        await Assert.That(created.Compatibility).IsEqualTo(Compatibility.FULL);
        await Assert.That(created.Description).IsEqualTo("Orders");
        await Assert.That(created.Tags["team"]).IsEqualTo("billing");
        await Assert.That(glue.Count("RegisterSchemaVersion")).IsEqualTo(2);
    }

    [Test]
    public async Task WithoutAutoRegistration_AMissingVersionIsReported()
    {
        using var glue = new InMemoryGlueClient();

        await Assert.That(async () => await new AvroSharpGlueSerializer(glue).SerializeAsync(NewOrder(), "orders"))
            .Throws<InvalidOperationException>().WithMessageContaining("auto-registration is off", StringComparison.Ordinal);
        await Assert.That(glue.Count("CreateSchema") + glue.Count("RegisterSchemaVersion")).IsEqualTo(0);
    }

    [Test]
    public async Task PendingVersion_IsWaitedFor_AndAFailedOneReported()
    {
        using var glue = new InMemoryGlueClient { PendingChecks = 2 };
        glue.Add("orders", OrderV1);
        var options = new AvroSharpGlueOptions { AutoRegisterSchemas = true, PendingVersionInterval = TimeSpan.FromMilliseconds(1) };

        var message = await new AvroSharpGlueSerializer(glue, options).SerializeAsync(NewOrder(), "orders");
        glue.FailNewVersions = true;
        var failing = async () => await new AvroSharpGlueSerializer(glue, options).SerializeAsync(new Item { Sku = "x", Quantity = 1 }, "orders");

        await Assert.That(message[0]).IsEqualTo((byte)0x03);
        await Assert.That(failing).Throws<InvalidOperationException>().WithMessageContaining("FAILURE", StringComparison.Ordinal);
    }

    [Test]
    public async Task SchemaNames_FromTheOptions_OrTheTransport()
    {
        using var glue = new InMemoryGlueClient();
        var definition = AvroTypes.Get<Order>().Schema.ToJson();
        var byTopic = glue.Add("orders-topic", definition);
        var fixedName = glue.Add("fixed", definition);
        var byRecord = glue.Add("test.glue.Order", definition);

        var a = await new AvroSharpGlueSerializer(glue).SerializeAsync(NewOrder(), "orders-topic");
        var b = await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { SchemaName = "fixed" }).SerializeAsync(NewOrder(), "orders-topic");
        var c = await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { SchemaNameStrategy = (_, schema) => ((Schemas.NamedSchema)schema).FullName }).SerializeAsync(NewOrder(), "orders-topic");

        await Assert.That(a.Take(18)).IsEquivalentTo(AwsMessage(byTopic, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(b.Take(18)).IsEquivalentTo(AwsMessage(fixedName, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(c.Take(18)).IsEquivalentTo(AwsMessage(byRecord, 0x00, []), CollectionOrdering.Matching);
    }

    [Test]
    public async Task RegistryCalls_AreMadeOncePerSchema()
    {
        using var glue = new InMemoryGlueClient();
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var writer = new AvroSharpGlueSerializer(glue);
        var reader = new AvroSharpGlueSerializer(glue);

        var messages = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            messages.Add(await writer.SerializeAsync(NewOrder(), "orders"));
        }

        foreach (var message in messages)
        {
            await reader.DeserializeAsync<Order>(message);
        }

        await Assert.That(glue.Count("GetSchemaByDefinition")).IsEqualTo(1);
        await Assert.That(glue.Count("GetSchemaVersion")).IsEqualTo(1);
    }

    [Test]
    public async Task KafkaAdapters_UseTheTopic_AndPassTombstones()
    {
        using var glue = new InMemoryGlueClient();
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var context = new SerializationContext(MessageComponentType.Value, "orders");
        var serializer = new AvroSharpGlueKafkaSerializer<Order>(glue);
        var deserializer = new AvroSharpGlueKafkaDeserializer<Order>(glue);

        var bytes = await serializer.SerializeAsync(NewOrder(), context);
        var tombstone = await serializer.SerializeAsync(null!, context);

        await AssertIsNewOrder(await deserializer.DeserializeAsync(bytes, isNull: false, context));
        await Assert.That(tombstone).IsNull();
        await Assert.That(await deserializer.DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, context)).IsNull();
        await Assert.That(async () => await new AvroSharpGlueKafkaDeserializer<int>(glue).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, context)).Throws<InvalidOperationException>();
        await Assert.That(new ProducerBuilder<string, Order>(new ProducerConfig()).SetAvroSharpGlueValueSerializer(glue)).IsNotNull();
        await Assert.That(new ConsumerBuilder<string, Order>(new ConsumerConfig()).SetAvroSharpGlueValueDeserializer(glue)).IsNotNull();
    }

    [Test]
    public async Task ReportsWhatItCantDo()
    {
        using var glue = new InMemoryGlueClient();
        var jsonVersion = glue.Add("events", """{"type":"object"}""", DataFormat.JSON);
        var serializer = new AvroSharpGlueSerializer(glue);

        await Assert.That(async () => await serializer.DeserializeAsync<Order>(AwsMessage(jsonVersion, 0x00, [0x00]))).Throws<InvalidOperationException>().WithMessageContaining("JSON", StringComparison.Ordinal);
        await Assert.That(async () => await serializer.DeserializeAsync<Order>(new byte[] { 0x00, 0x01, 0x02 })).Throws<AvroDataException>();
        await Assert.That(async () => await serializer.SerializeAsync(new Uri("https://example.com"), "orders")).Throws<InvalidOperationException>();
        await Assert.That(async () => await serializer.DeserializeAsync<Uri>(AwsMessage(jsonVersion, 0x00, [0x00]))).Throws<InvalidOperationException>();
    }

    // A message as AWS's encoder writes it.
    private static byte[] AwsMessage(string versionId, byte compression, byte[] data)
    {
        var uuid = Guid.Parse(versionId).ToString("N");
        var message = new byte[18 + data.Length];
        message[0] = 0x03;
        message[1] = compression;
        for (var i = 0; i < 16; i++)
        {
            message[2 + i] = Convert.ToByte(uuid.Substring(i * 2, 2), 16);
        }

        data.CopyTo(message, 18);
        return message;
    }

    private static byte[] ApacheBytes(Order value)
    {
        using var stream = new MemoryStream();
        new SpecificDatumWriter<Order>(value.Schema).Write(value, new BinaryEncoder(stream));
        return stream.ToArray();
    }

    private static byte[] ApacheBytes(ApacheGenericRecord value, Avro.Schema schema)
    {
        using var stream = new MemoryStream();
        new ApacheGenericWriter(schema).Write(value, new BinaryEncoder(stream));
        return stream.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Unzlib(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static Order NewOrder() => new()
    {
        Id = new Guid("0b7c6f0e-2d55-4a8e-9f6b-6f1d2c3b4a59"),
        Customer = "Ada",
        PlacedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        Total = new Avro.AvroDecimal(129.95m),
        Status = Status.PAID,
        Items = new List<Item> { new() { Sku = "SKU-1", Quantity = 2 }, new() { Sku = "SKU-2", Quantity = 1 } },
        Tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["channel"] = "web" },
        Note = "gift",
    };

    // The same record name with a different definition: a schema's first version, before NewOrder's.
    private static Item OldOrder() => new() { Sku = "first", Quantity = 1 };

    private static async Task AssertIsNewOrder(Order order)
    {
        var expected = NewOrder();
        await Assert.That(order.Id).IsEqualTo(expected.Id);
        await Assert.That(order.Customer).IsEqualTo(expected.Customer);
        await Assert.That(order.Total).IsEqualTo(expected.Total);
        await Assert.That(order.Items.Select(i => (i.Sku, i.Quantity))).IsEquivalentTo(expected.Items.Select(i => (i.Sku, i.Quantity)), CollectionOrdering.Matching);
        await Assert.That(order.Note).IsEqualTo(expected.Note);
    }
}
