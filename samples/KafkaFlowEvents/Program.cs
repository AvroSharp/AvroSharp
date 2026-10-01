// KafkaFlow with Confluent Schema Registry through AvroSharp.KafkaFlow: two event types on one topic, each under its own
// subject (the TopicRecord strategy), produced with KafkaFlow's producer and handled by a typed handler per type. It
// needs a broker and a registry: start Redpanda with `docker compose up -d --wait` in samples/Confluent, or set
// KAFKA_BOOTSTRAP_SERVERS and SCHEMA_REGISTRY_URL.
using System;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using AvroSharp.KafkaFlow;
using Confluent.SchemaRegistry;
using KafkaFlow;
using KafkaFlow.Producers;
using Microsoft.Extensions.DependencyInjection;
using Shop.Billing;
using Shop.Events;

var bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "127.0.0.1:19092";
var registryUrl = Environment.GetEnvironmentVariable("SCHEMA_REGISTRY_URL") ?? "http://127.0.0.1:18081";
var topic = "order-events-" + Guid.NewGuid().ToString("N")[..8];

using (var probe = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl }))
{
    try
    {
        await probe.GetAllSubjectsAsync();
    }
    catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"No schema registry at {registryUrl}. Start one with `docker compose up -d --wait` in samples/Confluent.");
        return 1;
    }
}

var services = new ServiceCollection();
services.AddSingleton<Received>();
services.AddKafka(kafka => kafka.AddCluster(cluster => cluster
    .WithBrokers([bootstrap])
    .WithSchemaRegistry(config => config.Url = registryUrl)
    .CreateTopicIfNotExists(topic, 1, 1)
    // The producer writes any type AvroSharp knows. With TopicRecord, each type's schema is registered under
    // "<topic>-<record name>", so one topic carries both.
    .AddProducer("events", producer => producer
        .DefaultTopic(topic)
        .AddMiddlewares(middlewares => middlewares
            .AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord })))
    // The consumer reads each message as the type whose record its writer's schema is, then passes it to that type's
    // handler.
    .AddConsumer(consumer => consumer
        .Topic(topic)
        .WithGroupId("billing-" + topic)
        .WithBufferSize(10)
        .WithWorkersCount(1)
        .WithAutoOffsetReset(AutoOffsetReset.Earliest)
        .AddMiddlewares(middlewares => middlewares
            .AddSchemaRegistryAvroSharpDeserializer([typeof(OrderPlaced), typeof(OrderShipped)])
            .AddTypedHandlers(handlers => handlers
                .AddHandler<OrderPlacedHandler>()
                .AddHandler<OrderShippedHandler>())))));

await using var provider = services.BuildServiceProvider();
var bus = provider.CreateKafkaBus();
await bus.StartAsync();

var events = provider.GetRequiredService<IProducerAccessor>().GetProducer("events");
var placedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
await events.ProduceAsync("order-1", new OrderPlaced { OrderId = 1, Customer = "Ada", Total = 129.95m, PlacedAt = placedAt });
await events.ProduceAsync("order-2", new OrderPlaced { OrderId = 2, Customer = "Grace", Total = 9.99m, PlacedAt = placedAt.AddMinutes(5) });
await events.ProduceAsync("order-1", new OrderShipped { OrderId = 1, Carrier = "DHL", TrackingNumber = "JD0146" });

var received = provider.GetRequiredService<Received>();
var deadline = DateTime.UtcNow.AddSeconds(60);
while (received.Messages.Count < 3 && DateTime.UtcNow < deadline)
{
    await Task.Delay(100);
}

await bus.StopAsync();

using (var registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registryUrl }))
{
    var subjects = (await registry.GetAllSubjectsAsync()).Where(s => s.StartsWith(topic, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    Console.WriteLine($"Subjects: {string.Join(", ", subjects)}");
    Check(subjects.SequenceEqual([$"{topic}-Shop.Events.OrderPlaced", $"{topic}-Shop.Events.OrderShipped"], StringComparer.Ordinal));
}

var placed = received.Messages.OfType<OrderPlaced>().ToList();
var shipped = received.Messages.OfType<OrderShipped>().ToList();
Check(placed.Count == 2 && placed.Sum(o => o.Total) == 139.94m && shipped.Count == 1 && string.Equals(shipped[0].TrackingNumber, "JD0146", StringComparison.Ordinal));

Console.WriteLine("OK");
return 0;

static void Check(bool condition)
{
    if (!condition)
    {
        Console.WriteLine("FAILED");
        Environment.Exit(1);
    }
}
