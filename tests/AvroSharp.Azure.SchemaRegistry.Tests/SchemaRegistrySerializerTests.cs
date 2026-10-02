using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging;
using Azure.Messaging.EventHubs;
using Microsoft.Azure.Data.SchemaRegistry.ApacheAvro;
using test.azure;
using TUnit.Assertions.Enums;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using GenericRecord = AvroSharp.Generic.GenericRecord;

namespace AvroSharp.Azure.SchemaRegistry.Tests;

/// <summary>
/// AvroSharp's serializer against Microsoft's Avro serializer (Microsoft.Azure.Data.SchemaRegistry.ApacheAvro), on one
/// registry: the same message body and content type, and each reads what the other writes.
/// </summary>
public class SchemaRegistrySerializerTests
{
    private const string Group = "orders";

    private const string OrderV1 = """
        {"type":"record","name":"Order","namespace":"test.azure","fields":[
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"customer","type":"string"},
          {"name":"placed_at","type":{"type":"long","logicalType":"timestamp-millis"}},
          {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
          {"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[
            {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]}
        """;

    [Test]
    public async Task WritesMicrosoftsBody_AndEachReadsTheOther()
    {
        var registry = new InMemorySchemaRegistryClient();
        var microsoft = new SchemaRegistryAvroSerializer(registry, Group, new SchemaRegistryAvroSerializerOptions { AutoRegisterSchemas = true });
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });

        var theirMessage = await microsoft.SerializeAsync<MessageContent, Order>(NewOrder());
        var ourMessage = await ours.SerializeAsync<MessageContent, Order>(NewOrder());

        await Assert.That(ourMessage.Data!.ToArray()).IsEquivalentTo(theirMessage.Data!.ToArray(), CollectionOrdering.Matching);
        await Assert.That(ourMessage.ContentType!.Value.ToString()).StartsWith("avro/binary+", StringComparison.Ordinal);
        await AssertIsNewOrder(await ours.DeserializeAsync<Order>(theirMessage));
        await AssertIsNewOrder(await microsoft.DeserializeAsync<Order>(ourMessage));
        // This registry matches schemas by their exact text, and the two write the same schema's text differently
        // (the key order, and the namespaces of nested types), so each registers a version of its own. Whether the
        // service compares text or meaning isn't documented: see "The schema text" in the guide.
        await Assert.That(ourMessage.ContentType!.Value.ToString()).IsNotEqualTo(theirMessage.ContentType!.Value.ToString());
    }

    [Test]
    public async Task OlderWriter_ReadsAsTheCurrentType()
    {
        var registry = new InMemorySchemaRegistryClient();
        var microsoft = new SchemaRegistryAvroSerializer(registry, Group, new SchemaRegistryAvroSerializerOptions { AutoRegisterSchemas = true });
        var old = new ApacheGenericRecord((Avro.RecordSchema)Avro.Schema.Parse(OrderV1));
        old.Add("id", Guid.NewGuid());
        old.Add("customer", "from v1");
        old.Add("placed_at", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        old.Add("total", new Avro.AvroDecimal(5.00m));
        old.Add("items", Array.Empty<object>());
        var message = await microsoft.SerializeAsync<MessageContent, ApacheGenericRecord>(old);

        var order = await new AvroSharpSchemaRegistrySerializer(registry).DeserializeAsync<Order>(message);

        await Assert.That(order.Customer).IsEqualTo("from v1");
        await Assert.That(order.Status).IsEqualTo(Status.NEW);
        await Assert.That(order.Note).IsNull();
    }

    [Test]
    public async Task GenericRecords_AreWrittenAndReadInTheWritersSchema()
    {
        var registry = new InMemorySchemaRegistryClient();
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });
        var schema = (Schemas.RecordSchema)Schemas.AvroSchema.Parse(OrderV1);
        var record = new GenericRecord(schema)
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["customer"] = "generic",
            ["placed_at"] = 0L,
            ["total"] = new byte[] { 0x01, 0xF4 },
            ["items"] = Generic.AvroValue.FromArray([]),
        };

        var message = await ours.SerializeAsync<MessageContent, GenericRecord>(record);
        var asRecord = await ours.DeserializeAsync<GenericRecord>(message);
        var asValue = (Generic.AvroValue)(await ours.DeserializeAsync(message, typeof(Generic.AvroValue)))!;
        var asOrder = await ours.DeserializeAsync<Order>(message);
        var byMicrosoft = await new SchemaRegistryAvroSerializer(registry).DeserializeAsync<ApacheGenericRecord>(message);

        await Assert.That(asRecord["customer"].AsString()).IsEqualTo("generic");
        await Assert.That(asValue.AsRecord().Schema.FullName).IsEqualTo("test.azure.Order");
        await Assert.That(asOrder.Total).IsEqualTo(new Avro.AvroDecimal(5.00m));
        await Assert.That(byMicrosoft["customer"]).IsEqualTo("generic");
    }

    [Test]
    public async Task WithoutAutoRegistration_TheSchemaIsLookedUp()
    {
        var registry = new InMemorySchemaRegistryClient();
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group);

        await Assert.That(async () => await ours.SerializeAsync<MessageContent, Order>(NewOrder())).Throws<RequestFailedException>();

        var id = registry.RegisterSchema(Group, "test.azure.Order", Serialization.AvroTypes.Get<Order>().Schema.ToJson(), global::Azure.Data.SchemaRegistry.SchemaFormat.Avro).Value.Id;
        var message = await ours.SerializeAsync<MessageContent, Order>(NewOrder());
        await Assert.That(message.ContentType!.Value.ToString()).IsEqualTo("avro/binary+" + id);
    }

    [Test]
    public async Task RegistryCalls_AreMadeOncePerSchema()
    {
        var registry = new InMemorySchemaRegistryClient();
        var writer = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });
        var reader = new AvroSharpSchemaRegistrySerializer(registry);

        var messages = new List<MessageContent>();
        for (var i = 0; i < 3; i++)
        {
            messages.Add(await writer.SerializeAsync<MessageContent, Order>(NewOrder()));
        }

        foreach (var message in messages)
        {
            await reader.DeserializeAsync<Order>(message);
        }

        await Assert.That(registry.RegistrationCalls).IsEqualTo(1);
        await Assert.That(registry.GetSchemaCalls).IsEqualTo(1);
    }

    [Test]
    public async Task SyncMethods_AndOtherMessageTypes()
    {
        var registry = new InMemorySchemaRegistryClient();
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });

        var eventData = ours.Serialize<EventData, Order>(NewOrder());
        var untyped = ours.Serialize(NewOrder(), messageType: typeof(EventData));
        var read = ours.Deserialize<Order>(eventData);
#pragma warning disable CA2263 // The overload by Type is under test.
        var readUntyped = (Order)ours.Deserialize(untyped, typeof(Order))!;
#pragma warning restore CA2263

        await Assert.That(eventData.ContentType).StartsWith("avro/binary+", StringComparison.Ordinal);
        await Assert.That(untyped).IsTypeOf<EventData>();
        await AssertIsNewOrder(read);
        await AssertIsNewOrder(readUntyped);
    }

    [Test]
    public async Task ADeclaredTypeAvroTypesDoesntKnow_WritesTheValuesOwnType()
    {
        var registry = new InMemorySchemaRegistryClient();
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });

        var byInterface = await ours.SerializeAsync<MessageContent, Avro.Specific.ISpecificRecord>(NewOrder());
        var byType = await ours.SerializeAsync(NewOrder(), typeof(Avro.Specific.ISpecificRecord));

        await AssertIsNewOrder(await ours.DeserializeAsync<Order>(byInterface));
        await AssertIsNewOrder(await ours.DeserializeAsync<Order>(byType));
    }

    [Test]
    public async Task ConcurrentFirstMessages_AllWriteAndRead()
    {
        var registry = new InMemorySchemaRegistryClient();
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });
        var reader = new AvroSharpSchemaRegistrySerializer(registry);

        var messages = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await ours.SerializeAsync<MessageContent, Order>(NewOrder()))));
        var orders = await Task.WhenAll(messages.Select(m => Task.Run(async () => await reader.DeserializeAsync<Order>(m))));

        await Assert.That(messages.Select(m => m.ContentType!.Value.ToString()).Distinct(StringComparer.Ordinal)).Count().IsEqualTo(1);
        foreach (var order in orders)
        {
            await AssertIsNewOrder(order);
        }
    }

    [Test]
    public async Task ReportsWhatItCantDo()
    {
        var registry = new InMemorySchemaRegistryClient();
        var readOnly = new AvroSharpSchemaRegistrySerializer(registry);
        var ours = new AvroSharpSchemaRegistrySerializer(registry, Group, new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });
        var valid = await ours.SerializeAsync<MessageContent, Order>(NewOrder());

        await Assert.That(async () => await readOnly.SerializeAsync<MessageContent, Order>(NewOrder())).Throws<InvalidOperationException>();
        await Assert.That(async () => await ours.SerializeAsync<MessageContent, string>("text")).Throws<ArgumentException>();
        await Assert.That(async () => await ours.SerializeAsync<MessageContent, Uri>(new Uri("https://example.com"))).Throws<InvalidOperationException>();
        await Assert.That(() => ours.Serialize(NewOrder(), messageType: typeof(string))).Throws<ArgumentException>();
        await Assert.That(() => ours.Serialize(NewOrder(), messageType: typeof(NoParameterlessConstructor)))
            .Throws<ArgumentException>().WithMessageContaining("parameterless", StringComparison.Ordinal);
        foreach (var contentType in new[] { "application/json", "avro/binary", "avro/binary+", "avro/json+1", "avro/binary+1+2" })
        {
            var message = new MessageContent { Data = valid.Data, ContentType = contentType };
            await Assert.That(async () => await ours.DeserializeAsync<Order>(message)).Throws<FormatException>().Because(contentType);
        }
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

    private static async Task AssertIsNewOrder(Order order)
    {
        var expected = NewOrder();
        await Assert.That(order.Id).IsEqualTo(expected.Id);
        await Assert.That(order.Customer).IsEqualTo(expected.Customer);
        await Assert.That(order.PlacedAt).IsEqualTo(expected.PlacedAt);
        await Assert.That(order.Total).IsEqualTo(expected.Total);
        await Assert.That(order.Status).IsEqualTo(expected.Status);
        await Assert.That(order.Items.Select(i => (i.Sku, i.Quantity))).IsEquivalentTo(expected.Items.Select(i => (i.Sku, i.Quantity)), CollectionOrdering.Matching);
        await Assert.That(order.Tags).IsEquivalentTo(expected.Tags, CollectionOrdering.Any);
        await Assert.That(order.Note).IsEqualTo(expected.Note);
    }
}

/// <summary>A message type the serializer can't create.</summary>
/// <param name="tag">Anything.</param>
public sealed class NoParameterlessConstructor(string tag) : MessageContent
{
    /// <summary>Gets the tag.</summary>
    public string Tag { get; } = tag;
}
