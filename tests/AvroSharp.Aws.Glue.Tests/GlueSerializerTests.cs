using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Glue;
using Avro.IO;
using Avro.Specific;
using AvroSharp.Aws.Glue.Kafka;
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
        await Assert.That(Unzlib(message.AsSpan(18).ToArray())).IsEquivalentTo(ApacheBytes(NewOrder()), CollectionOrdering.Matching);
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
    public async Task PendingVersion_ThatStaysPending_TimesOut()
    {
        using var glue = new InMemoryGlueClient { PendingChecks = 100 };
        glue.Add("orders", OrderV1);
        var options = new AvroSharpGlueOptions { AutoRegisterSchemas = true, PendingVersionInterval = TimeSpan.FromMilliseconds(1) };

        await Assert.That(async () => await new AvroSharpGlueSerializer(glue, options).SerializeAsync(NewOrder(), "orders"))
            .Throws<TimeoutException>().WithMessageContaining("still PENDING after 10 checks", StringComparison.Ordinal);
        await Assert.That(glue.Count("GetSchemaVersion")).IsEqualTo(10);
    }

    [Test]
    public async Task ASchemaCreatedElsewhereMeanwhile_GetsTheVersion()
    {
        using var glue = new InMemoryGlueClient { CreatedElsewhere = true };
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true });

        var message = await serializer.SerializeAsync(NewOrder(), "orders");

        await AssertIsNewOrder(await serializer.DeserializeAsync<Order>(message));
        await Assert.That(glue.Calls).IsEquivalentTo(["GetSchemaByDefinition", "RegisterSchemaVersion", "CreateSchema", "RegisterSchemaVersion", "PutSchemaVersionMetadata"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnAnswerWithoutAVersionId_IsReported()
    {
        using var glue = new InMemoryGlueClient { WithoutVersionIds = true };
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());

        await Assert.That(async () => await new AvroSharpGlueSerializer(glue).SerializeAsync(NewOrder(), "orders"))
            .Throws<InvalidOperationException>().WithMessageContaining("without a valid schema version ID", StringComparison.Ordinal);
    }

    [Test]
    public async Task Options_ChangedAfterwards_DontChangeTheSerializer()
    {
        using var glue = new InMemoryGlueClient();
        var id = glue.Add("fixed", AvroTypes.Get<Order>().Schema.ToJson());
        var options = new AvroSharpGlueOptions { SchemaName = "fixed" };
        var serializer = new AvroSharpGlueSerializer(glue, options);

        options.SchemaName = "other";
        options.Compression = AvroSharpGlueCompression.Zlib;
        var message = await serializer.SerializeAsync(NewOrder(), "orders");

        await Assert.That(message.Take(18)).IsEquivalentTo(AwsMessage(id, 0x00, []), CollectionOrdering.Matching);
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
        var c = await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { SchemaNameStrategy = naming => ((Schemas.NamedSchema)naming.Schema).FullName }).SerializeAsync(NewOrder(), "orders-topic");

        await Assert.That(a.Take(18)).IsEquivalentTo(AwsMessage(byTopic, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(b.Take(18)).IsEquivalentTo(AwsMessage(fixedName, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(c.Take(18)).IsEquivalentTo(AwsMessage(byRecord, 0x00, []), CollectionOrdering.Matching);
    }

    [Test]
    public async Task SchemaNameStrategy_TellsKeysFromValues()
    {
        using var glue = new InMemoryGlueClient();
        var definition = AvroTypes.Get<Order>().Schema.ToJson();
        var keyVersion = glue.Add("orders-key", definition);
        var valueVersion = glue.Add("orders-value", definition);
        var options = new AvroSharpGlueOptions { SchemaNameStrategy = naming => naming.TransportName + (naming.IsKey ? "-key" : "-value") };
        var serializer = new AvroSharpGlueKafkaSerializer<Order>(glue, options);

        var key = serializer.Serialize(NewOrder(), new SerializationContext(MessageComponentType.Key, "orders"));
        var value = serializer.Serialize(NewOrder(), new SerializationContext(MessageComponentType.Value, "orders"));
        var direct = await new AvroSharpGlueSerializer(glue, options).SerializeAsync(NewOrder(), "orders", isKey: true);

        await Assert.That(key!.Take(18)).IsEquivalentTo(AwsMessage(keyVersion, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(value!.Take(18)).IsEquivalentTo(AwsMessage(valueVersion, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(direct.Take(18)).IsEquivalentTo(AwsMessage(keyVersion, 0x00, []), CollectionOrdering.Matching);
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
        var producer = new ProducerBuilder<string, Order>(new ProducerConfig());
        var consumer = new ConsumerBuilder<string, Order>(new ConsumerConfig());
        await Assert.That(producer.SetAvroSharpGlueValueSerializer(glue)).IsSameReferenceAs(producer);
        await Assert.That(consumer.SetAvroSharpGlueValueDeserializer(glue)).IsSameReferenceAs(consumer);
    }

    [Test]
    public async Task KafkaAdapters_AreSynchronousToo()
    {
        using var glue = new InMemoryGlueClient();
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var context = new SerializationContext(MessageComponentType.Value, "orders");
        var serializer = new AvroSharpGlueKafkaSerializer<Order>(glue);
        var deserializer = new AvroSharpGlueKafkaDeserializer<Order>(glue);
        var generic = new AvroSharpGlueKafkaSerializer<AvroValue>(glue);

        var bytes = serializer.Serialize(NewOrder(), context);

        await AssertIsNewOrder(deserializer.Deserialize(bytes, isNull: false, context));
        await Assert.That(serializer.Serialize(null!, context)).IsNull();
        await Assert.That(generic.Serialize(AvroValue.Null, context)).IsNull();
        await Assert.That(deserializer.Deserialize(default, isNull: true, context)).IsNull();
        await Assert.That(() => new AvroSharpGlueKafkaDeserializer<int>(glue).Deserialize(default, isNull: true, context)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AVersionAnotherProducerIsRegistering_IsWaitedFor()
    {
        using var glue = new InMemoryGlueClient();
        var id = glue.AddPending("orders", AvroTypes.Get<Order>().Schema.ToJson(), checks: 2);
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { PendingVersionInterval = TimeSpan.FromMilliseconds(1) });

        var message = await serializer.SerializeAsync(NewOrder(), "orders");

        await Assert.That(message.Take(18)).IsEquivalentTo(AwsMessage(id, 0x00, []), CollectionOrdering.Matching);
        await Assert.That(glue.Count("GetSchemaVersion")).IsEqualTo(2);
        await Assert.That(glue.Count("RegisterSchemaVersion") + glue.Count("CreateSchema")).IsEqualTo(0);
    }

    [Test]
    public async Task RegisteredVersions_GetTheTransportAndTheUsersMetadata()
    {
        using var glue = new InMemoryGlueClient();
        var options = new AvroSharpGlueOptions
        {
            AutoRegisterSchemas = true,
            SchemaName = "orders-value",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["team"] = "billing" },
        };
        var found = glue.Add("found", AvroTypes.Get<Order>().Schema.ToJson());

        var message = await new AvroSharpGlueSerializer(glue, options).SerializeAsync(NewOrder(), "orders");
        await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { SchemaName = "found" }).SerializeAsync(NewOrder(), "orders");

        var id = GlueSerializerTests.BigEndianGuid(message.AsSpan(2, 16)).ToString();
        await Assert.That(glue.Metadata[id]).IsEquivalentTo(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["x-amz-meta-transport"] = "orders", ["team"] = "billing" }, CollectionOrdering.Any);
        // Only versions the serializer registers get metadata, as with AWS's serializer.
        await Assert.That(glue.Metadata.ContainsKey(found)).IsFalse();
    }

    [Test]
    public async Task AnUnknownVersionId_IsAnAvroDataException()
    {
        using var glue = new InMemoryGlueClient();

        await Assert.That(async () => await new AvroSharpGlueSerializer(glue).DeserializeAsync<Order>(AwsMessage(Guid.NewGuid().ToString(), 0x00, [0x00])))
            .Throws<AvroDataException>().WithMessageContaining("unknown, or was deleted", StringComparison.Ordinal);
    }

    [Test]
    public async Task AVersionAnotherProducerIsRegistering_ThatFails_IsReported()
    {
        using var glue = new InMemoryGlueClient { FailNewVersions = true };
        glue.AddPending("orders", AvroTypes.Get<Order>().Schema.ToJson(), checks: 1);
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { PendingVersionInterval = TimeSpan.FromMilliseconds(1) });

        await Assert.That(async () => await serializer.SerializeAsync(NewOrder(), "orders"))
            .Throws<InvalidOperationException>().WithMessageContaining("FAILURE", StringComparison.Ordinal);
    }

    [Test]
    public async Task GenericValues_AndValuesTypedAsObject()
    {
        using var glue = new InMemoryGlueClient();
        var serializer = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true });
        var record = await serializer.DeserializeAsync<GenericRecord>(await serializer.SerializeAsync<object>(NewOrder(), "orders"));

        var again = await serializer.SerializeAsync(AvroValue.FromRecord(record), "orders");

        await AssertIsNewOrder(await serializer.DeserializeAsync<Order>(again));
        await Assert.That(async () => await serializer.SerializeAsync(AvroValue.FromInt32(1), "orders"))
            .Throws<ArgumentException>().WithMessageContaining("must be a record", StringComparison.Ordinal);
    }

    [Test]
    public async Task Cancellation_AndWhatTheServiceRejects()
    {
        using var glue = new InMemoryGlueClient { Registries = ["default-registry"] };
        glue.Add("events", """{"type":"object"}""", DataFormat.JSON);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var autoRegister = new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true });

        await Assert.That(async () => await autoRegister.SerializeAsync(NewOrder(), "orders", canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true, RegistryName = "missing" }).SerializeAsync(NewOrder(), "orders"))
            .Throws<InvalidOperationException>().WithMessageContaining("registry 'missing' doesn't exist", StringComparison.Ordinal);
        // An Avro schema under the name of a JSON Schema: the service rejects the version.
        await Assert.That(async () => await autoRegister.SerializeAsync(NewOrder(), "events")).Throws<Amazon.Glue.Model.InvalidInputException>();
    }

    [Test]
    public async Task ConcurrentFirstWrites_LookTheSchemaUpOnce()
    {
        using var glue = new InMemoryGlueClient();
        glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var serializer = new AvroSharpGlueSerializer(glue);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await serializer.SerializeAsync(NewOrder(), "orders"))));

        await Assert.That(glue.Count("GetSchemaByDefinition")).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentFirstMessages_AllWriteAndRead()
    {
        using var glue = new InMemoryGlueClient();
        var id = glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var serializer = new AvroSharpGlueSerializer(glue);

        var messages = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await serializer.SerializeAsync(NewOrder(), "orders"))));
        var orders = await Task.WhenAll(messages.Select(m => Task.Run(async () => await serializer.DeserializeAsync<Order>(m))));

        await Assert.That(messages.Select(m => Convert.ToHexString(m.AsSpan(0, 18))).Distinct(StringComparer.Ordinal)).IsEquivalentTo([Convert.ToHexString(AwsMessage(id, 0x00, []))], CollectionOrdering.Any);
        foreach (var order in orders)
        {
            await AssertIsNewOrder(order);
        }
    }

    [Test]
    public async Task ReportsWhatItCantDo()
    {
        using var glue = new InMemoryGlueClient();
        var jsonVersion = glue.Add("events", """{"type":"object"}""", DataFormat.JSON);
        var orderVersion = glue.Add("orders", AvroTypes.Get<Order>().Schema.ToJson());
        var serializer = new AvroSharpGlueSerializer(glue);

        await Assert.That(async () => await serializer.DeserializeAsync<Order>(AwsMessage(jsonVersion, 0x00, [0x00]))).Throws<InvalidOperationException>().WithMessageContaining("JSON", StringComparison.Ordinal);
        await Assert.That(async () => await serializer.DeserializeAsync<Order>(new byte[] { 0x00, 0x01, 0x02 })).Throws<AvroDataException>();
        await Assert.That(async () => await serializer.SerializeAsync(new Uri("https://example.com"), "orders")).Throws<InvalidOperationException>().WithMessageContaining("not a type AvroSharp knows", StringComparison.Ordinal);
        await Assert.That(async () => await serializer.DeserializeAsync<Uri>(AwsMessage(orderVersion, 0x00, [0x00]))).Throws<InvalidOperationException>().WithMessageContaining("not a type AvroSharp knows", StringComparison.Ordinal);
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

    // zlib (RFC 1950): ZLibStream from .NET 6; on .NET Framework, a deflate stream with zlib's 2-byte header and its
    // Adler-32 trailer.
    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
#if NET6_0_OR_GREATER
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }
#else
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        output.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler], 0, 4);
#endif
        return output.ToArray();
    }

    private static byte[] Unzlib(byte[] data)
    {
#if NET6_0_OR_GREATER
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
#else
        using var input = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress);
#endif
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>A GUID from its 16 big-endian bytes, as AWS's wire format writes it (<c>new Guid(bytes, bigEndian: true)</c> from .NET 8).</summary>
    internal static Guid BigEndianGuid(ReadOnlySpan<byte> bytes)
    {
        Span<byte> little = stackalloc byte[16];
        bytes[..16].CopyTo(little);
        little[..4].Reverse();
        little.Slice(4, 2).Reverse();
        little.Slice(6, 2).Reverse();
        return new Guid(little.ToArray());
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
