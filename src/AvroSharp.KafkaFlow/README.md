![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/banner.png)

# AvroSharp.KafkaFlow

[KafkaFlow](https://github.com/Farfetch/kafkaflow) serializer middleware for Confluent Schema Registry, on [AvroSharp](https://github.com/AvroSharp/AvroSharp) and [AvroSharp.Confluent](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Confluent), without Apache.Avro. A replacement for KafkaFlow's `KafkaFlow.Serializer.SchemaRegistry.ConfluentAvro`:
- **The same bytes and subjects** as KafkaFlow's Confluent Avro serializer. The tests compare them byte for byte, and each reads what the other writes.
- **The same setup:** KafkaFlow's `WithSchemaRegistry` on the cluster, and `AddSchemaRegistryAvroSharpSerializer` and `AddSchemaRegistryAvroSharpDeserializer` in place of KafkaFlow's methods.
- **Types:** generated from `.avsc` files, or your own C# types marked `[AvroSerializable]`. Several record types can share one topic, each read as its own type.
- **Schema evolution:** each message is read in its writer's schema and resolved to your type's.

> **Status:** new in the 1.0.0 release candidates, and released with AvroSharp at the same version. Its API may still change until 1.0.0; from then it follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/kafkaflow.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.KafkaFlow.html) · [AvroSharp.Confluent's guide](https://avrosharp.github.io/AvroSharp/docs/confluent.html) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.KafkaFlow --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.KafkaFlow depends on KafkaFlow 4.0.0 or later (below 5.0), and targets .NET 8 and later (no .NET Framework). KafkaFlow's assemblies aren't strong-named, so neither is this one.

## Use

```csharp
services.AddKafka(kafka => kafka.AddCluster(cluster => cluster
    .WithBrokers(["localhost:9092"])
    .WithSchemaRegistry(config => config.Url = "http://localhost:8081")
    .AddProducer("orders", producer => producer
        .DefaultTopic("orders")
        .AddMiddlewares(middlewares => middlewares.AddSchemaRegistryAvroSharpSerializer()))
    .AddConsumer(consumer => consumer
        .Topic("orders")
        .WithGroupId("billing")
        .WithBufferSize(100)
        .WithWorkersCount(10)
        .AddMiddlewares(middlewares => middlewares
            .AddSchemaRegistryAvroSharpDeserializer<Order>()
            .AddTypedHandlers(handlers => handlers.AddHandler<OrderHandler>())))));
```

For a topic with several record types, pass them to the deserializer: `AddSchemaRegistryAvroSharpDeserializer([typeof(OrderPlaced), typeof(OrderShipped)])`. The [guide](https://avrosharp.github.io/AvroSharp/docs/kafkaflow.html) covers that, the settings, and moving from KafkaFlow's Confluent Avro serializer.
