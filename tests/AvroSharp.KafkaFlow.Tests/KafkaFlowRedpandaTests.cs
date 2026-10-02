using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using AvroSharp.Confluent.Tests;
using Confluent.SchemaRegistry;
using KafkaFlow;
using KafkaFlow.Configuration;
using KafkaFlow.Producers;
using Microsoft.Extensions.DependencyInjection;
using test.shop;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.KafkaFlow.Tests;

/// <summary>
/// A KafkaFlow producer and consumer against a real broker and schema registry (Redpanda in Docker). They need Docker
/// with Linux containers, so they run only when a filter names them:
/// dotnet test --project tests/AvroSharp.KafkaFlow.Tests --treenode-filter "/*/*/KafkaFlowRedpandaTests/*".
/// </summary>
[Explicit]
[NotInParallel]
[ClassDataSource<RedpandaFixture>(Shared = SharedType.PerClass)]
public class KafkaFlowRedpandaTests(RedpandaFixture redpanda)
{
    private static readonly Cart SampleCart = new() { Owner = "Ada", Lines = [new CartLine { Sku = "SKU-9", Quantity = 9 }] };

    [Test]
    public async Task SeveralTypesOnOneTopic_ProducedAndConsumed()
    {
        var topic = Topic();
        var received = await RoundTrip(
            topic,
            middlewares => middlewares.AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord }),
            middlewares => middlewares.AddSchemaRegistryAvroSharpDeserializer([typeof(Order), typeof(Cart)]),
            [NewOrder(), SampleCart]);

        await KafkaFlowSerializerTests.AssertIsNewOrder(received.OfType<Order>().Single());
        await Assert.That(received.OfType<Cart>().Single().Lines.Single().Sku).IsEqualTo("SKU-9");
    }

    [Test]
    public async Task KafkaFlowsConfluentAvroProducer_ReadByAvroSharpsConsumer()
    {
        var topic = Topic();
        var received = await RoundTrip(
            topic,
            middlewares => middlewares.AddSchemaRegistryAvroSerializer(),
            middlewares => middlewares.AddSchemaRegistryAvroSharpDeserializer<Order>(),
            [NewOrder()]);

        await KafkaFlowSerializerTests.AssertIsNewOrder((Order)received.Single());
    }

    [Test]
    public async Task AvroSharpsProducer_ReadByKafkaFlowsConfluentAvroConsumer()
    {
        var topic = Topic();
        var received = await RoundTrip(
            topic,
            middlewares => middlewares.AddSchemaRegistryAvroSharpSerializer(),
            middlewares => middlewares.AddSchemaRegistryAvroDeserializer(),
            [NewOrder()]);

        await KafkaFlowSerializerTests.AssertIsNewOrder((Order)received.Single());
    }

    [Test]
    public async Task TypesFoundByName_ProducedAndConsumed()
    {
        var topic = Topic();
#pragma warning disable IL2026 // The test types are in this assembly, which isn't trimmed.
        var received = await RoundTrip(
            topic,
            middlewares => middlewares.AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord }),
            middlewares => middlewares.AddSchemaRegistryAvroSharpDeserializerByTypeName(),
            [NewOrder(), SampleCart]);
#pragma warning restore IL2026

        await KafkaFlowSerializerTests.AssertIsNewOrder(received.OfType<Order>().Single());
        await Assert.That(received.OfType<Cart>().Single().Owner).IsEqualTo("Ada");
    }

    // Produces the messages through a KafkaFlow bus and returns what its consumer read, in order.
    private async Task<object[]> RoundTrip(
        string topic,
        Action<IProducerMiddlewareConfigurationBuilder> producerMiddlewares,
        Action<IConsumerMiddlewareConfigurationBuilder> consumerMiddlewares,
        object[] messages)
    {
        var received = new ConcurrentQueue<object>();
        var services = new ServiceCollection();
        services.AddKafka(kafka => kafka.AddCluster(cluster => cluster
            .WithBrokers([redpanda.BootstrapServers])
            .WithSchemaRegistry(config => config.Url = redpanda.SchemaRegistryAddress)
            .CreateTopicIfNotExists(topic, 1, 1)
            .AddProducer(topic, producer => producer.DefaultTopic(topic).AddMiddlewares(producerMiddlewares))
            .AddConsumer(consumer => consumer
                .Topic(topic)
                .WithGroupId(topic)
                .WithBufferSize(10)
                .WithWorkersCount(1)
                .WithAutoOffsetReset(AutoOffsetReset.Earliest)
                .AddMiddlewares(middlewares =>
                {
                    consumerMiddlewares(middlewares);
                    middlewares.Add(_ => new Collect(received));
                }))));

        await using var provider = services.BuildServiceProvider();
        var bus = provider.CreateKafkaBus();
        await bus.StartAsync();
        try
        {
            var producer = provider.GetRequiredService<IProducerAccessor>().GetProducer(topic);
            for (var i = 0; i < messages.Length; i++)
            {
                await producer.ProduceAsync($"key-{i}", messages[i]);
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (received.Count < messages.Length && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
        finally
        {
            await bus.StopAsync();
        }

        await Assert.That(received.Count).IsEqualTo(messages.Length);
        return [.. received];
    }

    // The end of the consumer's middlewares: keeps each deserialized message.
    private sealed class Collect(ConcurrentQueue<object> received) : IMessageMiddleware
    {
        public Task Invoke(IMessageContext context, MiddlewareDelegate next)
        {
            received.Enqueue(context.Message.Value);
            return Task.CompletedTask;
        }
    }
}
