using System;
using System.Threading.Tasks;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// Against a real broker and schema registry (Redpanda in Docker): registration, the subject name strategy's fallback,
/// and a produce and consume through Confluent.Kafka. They need Docker with Linux containers, so they run only when
/// a filter names them: dotnet test --project tests/AvroSharp.Confluent.Tests --treenode-filter "/*/*/RedpandaTests/*".
/// Otherwise they aren't run or listed.
/// </summary>
[Explicit]
// One at a time: the registry can give one schema two IDs when tests register it at the same moment in different
// subjects, and the tests compare IDs.
[NotInParallel]
[ClassDataSource<RedpandaFixture>(Shared = SharedType.PerClass)]
public class RedpandaTests(RedpandaFixture redpanda)
{
    [Test]
    public async Task BothSerializers_ShareOneVersion_AndWriteTheSameBytes()
    {
        using var registry = redpanda.Registry();
        var topic = Topic();

        var theirs = await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));
        var ours = await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        await Assert.That(ours).IsEquivalentTo(theirs, CollectionOrdering.Matching);
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task EachReadsTheOther()
    {
        using var registry = redpanda.Registry();
        var topic = Topic();

        var ours = await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));
        var theirs = await new AvroSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));

        await ConfluentInteropTests.AssertIsNewOrder(await new AvroDeserializer<Order>(registry).DeserializeAsync(ours, isNull: false, Value(topic)));
        await ConfluentInteropTests.AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(theirs, isNull: false, Value(topic)));
    }

    [Test]
    public async Task OlderWriter_ReadsAsTheCurrentType()
    {
        using var registry = redpanda.Registry();
        var topic = Topic();
        var old = new GenericRecord((Avro.RecordSchema)Avro.Schema.Parse(OrderV1));
        old.Add("id", Guid.NewGuid());
        old.Add("customer", "from v1");
        old.Add("placed_at", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        old.Add("total", new Avro.AvroDecimal(5.00m));
        old.Add("items", Array.Empty<object>());
        var bytes = await new AvroSerializer<GenericRecord>(registry).SerializeAsync(old, Value(topic));

        var order = await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(order.Customer).IsEqualTo("from v1");
        await Assert.That(order.Status).IsEqualTo(Status.NEW);
    }

    [Test]
    public async Task ProduceAndConsume()
    {
        using var registry = redpanda.Registry();
        var topic = Topic();

        using (var producer = new ProducerBuilder<string, Order>(new ProducerConfig { BootstrapServers = redpanda.BootstrapServers })
            .SetAvroSharpKeySerializer(registry)
            .SetAvroSharpValueSerializer(registry)
            .Build())
        {
            await producer.ProduceAsync(topic, new Message<string, Order> { Key = "order-1", Value = NewOrder() });

            // Produce, which needs synchronous serializers.
            DeliveryReport<string, Order>? report = null;
            producer.Produce(topic, new Message<string, Order> { Key = "order-2", Value = NewOrder() }, r => report = r);
            producer.Flush(TimeSpan.FromSeconds(30));
            await Assert.That(report?.Error.IsError).IsFalse();
        }

        using var consumer = new ConsumerBuilder<string, Order>(new ConsumerConfig { BootstrapServers = redpanda.BootstrapServers, GroupId = topic, AutoOffsetReset = AutoOffsetReset.Earliest })
            .SetAvroSharpKeyDeserializer(registry)
            .SetAvroSharpValueDeserializer(registry)
            .Build();
        consumer.Subscribe(topic);
        var result = consumer.Consume(TimeSpan.FromSeconds(30));
        var second = consumer.Consume(TimeSpan.FromSeconds(30));
        consumer.Close();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Message.Key).IsEqualTo("order-1");
        await ConfluentInteropTests.AssertIsNewOrder(result.Message.Value);
        await Assert.That(second?.Message.Key).IsEqualTo("order-2");
        await ConfluentInteropTests.AssertIsNewOrder(second!.Message.Value);
    }
}
