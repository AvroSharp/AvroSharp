// Kafka with Confluent Schema Registry through AvroSharp.Confluent: produce [AvroSerializable] orders, consume them as
// the same type, as generic values, and as a newer version of the type. It needs a broker and a registry: start
// Redpanda with `docker compose up -d --wait` in this folder, or set KAFKA_BOOTSTRAP_SERVERS and SCHEMA_REGISTRY_URL.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using AvroSharp.Generic;
using Billing;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Shop;

var bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "127.0.0.1:19092";
var registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL") ?? "http://127.0.0.1:18081";
var topic = "orders-" + Guid.NewGuid().ToString("N")[..8];

using var registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl });
try
{
    await registry.GetAllSubjectsAsync();
}
catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
{
    Console.WriteLine($"No schema registry at {registryUrl}. Start one with `docker compose up -d --wait` in samples/Confluent.");
    return 1;
}

var orders = Enumerable.Range(1, 3).Select(i => new Order
{
    Id = i,
    Customer = $"customer-{i}",
    PlacedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(i),
    Total = 9.99m * i,
    Lines = [new OrderLine { Sku = "SKU-" + i, Quantity = i }],
}).ToList();

// Produce: the serializer registers Order's schema under the subject "<topic>-value" (and the key's, a string, under
// "<topic>-key") the first time, and prefixes each message with the schema's ID, as Confluent's serializer does.
using (var producer = new ProducerBuilder<string, Order>(new ProducerConfig { BootstrapServers = bootstrap, MessageTimeoutMs = 30_000 })
    .SetAvroSharpKeySerializer(registry)
    .SetAvroSharpValueSerializer(registry)
    .Build())
{
    foreach (var order in orders)
    {
        await producer.ProduceAsync(topic, new Message<string, Order> { Key = $"order-{order.Id}", Value = order });
    }
}

var registered = await registry.GetLatestSchemaAsync(topic + "-value");
Console.WriteLine($"Registered {registered.Subject} version {registered.Version}, schema ID {registered.Id}");
Check(registered.SchemaString.Contains("\"name\":\"Order\"", StringComparison.Ordinal));

// Consume as the same type: the deserializer fetches the writer's schema by the message's ID (once) and reads it.
var read = Consume(new ConsumerBuilder<string, Order>(ConsumerConfig(bootstrap)).SetAvroSharpKeyDeserializer(registry).SetAvroSharpValueDeserializer(registry));
Console.WriteLine($"Read {read.Count} orders: {string.Join(", ", read.Select(m => $"{m.Key} = {m.Value.Total}"))}");
Check(read.Count == orders.Count && read.Select(m => m.Value.Total).SequenceEqual(orders.Select(o => o.Total)) && string.Equals(read[0].Value.Lines[0].Sku, "SKU-1", StringComparison.Ordinal));

// Consume as generic values, for code that has no type for the schema: each message in its writer's schema.
var generic = Consume(new ConsumerBuilder<string, AvroValue>(ConsumerConfig(bootstrap)).SetKeyDeserializer(Deserializers.Utf8)
    .SetAvroSharpGenericValueDeserializer(registry));
var first = generic[0].Value.AsRecord();
Console.WriteLine($"As a generic record: {first.Schema.FullName}, customer {first["customer"].AsString()}");
Check(string.Equals(first["customer"].AsString(), "customer-1", StringComparison.Ordinal));

// Consume as a newer version of the type, in a service that was updated first: the data is resolved to its schema,
// so the new field gets its default and the field it lacks is skipped.
var newer = Consume(new ConsumerBuilder<string, OrderV2>(ConsumerConfig(bootstrap)).SetAvroSharpKeyDeserializer(registry).SetAvroSharpValueDeserializer(registry));
Console.WriteLine($"As OrderV2: {newer[0].Value.Customer}, {newer[0].Value.Total} {newer[0].Value.Currency}");
Check(newer.Count == orders.Count && string.Equals(newer[0].Value.Currency, "EUR", StringComparison.Ordinal) && newer[2].Value.Total == orders[2].Total);

Console.WriteLine("OK");
return 0;

// Reads the topic from the start until it has every order, in a consumer group of its own.
List<Message<string, T>> Consume<T>(ConsumerBuilder<string, T> builder)
{
    using var consumer = builder.Build();
    consumer.Subscribe(topic);
    var messages = new List<Message<string, T>>();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (messages.Count < orders.Count && DateTime.UtcNow < deadline)
    {
        if (consumer.Consume(TimeSpan.FromSeconds(1)) is { } result)
        {
            messages.Add(result.Message);
        }
    }

    consumer.Close();
    return messages;
}

static ConsumerConfig ConsumerConfig(string bootstrap) => new()
{
    BootstrapServers = bootstrap,
    GroupId = "sample-" + Guid.NewGuid().ToString("N"),
    AutoOffsetReset = AutoOffsetReset.Earliest,
};

static void Check(bool condition)
{
    if (!condition)
    {
        Console.WriteLine("FAILED");
        Environment.Exit(1);
    }
}
