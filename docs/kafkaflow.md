# KafkaFlow

[AvroSharp.KafkaFlow](https://www.nuget.org/packages/AvroSharp.KafkaFlow) is serializer middleware for [KafkaFlow](https://github.com/Farfetch/kafkaflow) producers and consumers, with Confluent Schema Registry. It replaces KafkaFlow's `KafkaFlow.Serializer.SchemaRegistry.ConfluentAvro`, without Apache.Avro:
- **The same bytes** as KafkaFlow's Confluent Avro serializer, so producers and consumers can move one at a time.
- **The same registry setting:** KafkaFlow's `WithSchemaRegistry` on the cluster.
- **Several record types on one topic**, picked from each message's writer schema.

It's built on [AvroSharp.Confluent](confluent.md), whose settings, rules and behavior apply.

```
dotnet add package AvroSharp.KafkaFlow --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.KafkaFlow depends on KafkaFlow 4.0.0 or later (below 5.0), and is released with AvroSharp, at the same version. It targets .NET 8 and later. KafkaFlow's assemblies aren't strong-named, so neither is this one.

On this page:
- [Use](#use)
- [Several types on one topic](#several-types-on-one-topic)
- [Moving from KafkaFlow's Confluent Avro serializer](#moving-from-kafkaflows-confluent-avro-serializer)
- [Behavior to know](#behavior-to-know)

## Use

The message types are types generated from `.avsc` files, or your own types marked `[AvroSerializable]` (see [code generation](code-generation.md)).

```csharp
using AvroSharp.Confluent;
using AvroSharp.KafkaFlow;
using Confluent.SchemaRegistry;
using KafkaFlow;

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

- **The producer** writes each message of any type that AvroSharp knows. It registers the type's schema under the subject the first time, as KafkaFlow's serializer does.
- **The consumer** reads each message as `Order`, whichever version of the schema wrote it. Each message is read in its writer's schema, fetched from the registry by the message's schema ID, and resolved to `Order`'s schema.
- **Settings:** both methods take AvroSharp.Confluent's settings (`AvroSharpSerializerConfig`, `AvroSharpDeserializerConfig`), with Confluent's keys. See [the settings](confluent.md#settings).

The middlewares are [`AvroSharpKafkaFlowSerializer`](xref:AvroSharp.KafkaFlow.AvroSharpKafkaFlowSerializer) and [`AvroSharpKafkaFlowDeserializer`](xref:AvroSharp.KafkaFlow.AvroSharpKafkaFlowDeserializer). You can also use them yourself with KafkaFlow's `SerializerProducerMiddleware` and `DeserializerConsumerMiddleware`.

## Several types on one topic

A topic can carry several record types, each under its own subject with the `TopicRecord` or `Record` strategy. The consumer then picks each message's type from its writer's schema:

```csharp
// Producer: each type's schema under "<topic>-<record name>".
middlewares.AddSchemaRegistryAvroSharpSerializer(new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });

// Consumer: the type whose record the message's schema is.
middlewares
    .AddSchemaRegistryAvroSharpDeserializer([typeof(OrderPlaced), typeof(OrderShipped)])
    .AddTypedHandlers(handlers => handlers.AddHandler<OrderPlacedHandler>().AddHandler<OrderShippedHandler>());
```

- **With a list of types,** [`AvroSharpMessageTypeResolver`](xref:AvroSharp.KafkaFlow.AvroSharpMessageTypeResolver) matches each type by its schema's record name. A type's .NET name can differ from its Avro name. There's no reflection, so this works with trimming and Native AOT.
- **Without a list,** `AddSchemaRegistryAvroSharpDeserializer()` finds the type by its .NET full name in the loaded assemblies, as KafkaFlow's `AddSchemaRegistryAvroDeserializer()` does. That works when the .NET names match the Avro names, as they do by default for generated types. It's marked as unsafe for trimming.

## Moving from KafkaFlow's Confluent Avro serializer

1. **Generate the types with AvroSharp.Generators.**
   - **Moving gradually?** Set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, and the generated classes also implement Apache's `ISpecificRecord`. Both serializers then work with the same classes, as this repository's tests use them. See [the Apache.Avro compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode).
   - **Moving at once?** Use AvroSharp's own types, without Apache.Avro.
2. **Replace the middleware:**
   - `AddSchemaRegistryAvroSerializer(config)` → `AddSchemaRegistryAvroSharpSerializer(config)`, with `AvroSharpSerializerConfig` in place of `AvroSerializerConfig`, and the same keys;
   - `AddSchemaRegistryAvroDeserializer()` → `AddSchemaRegistryAvroSharpDeserializer()`, or the overload that takes the message types;
   - keep `WithSchemaRegistry` on the cluster.
3. **Nothing changes on the wire or in the registry:**
   - the same subjects;
   - the same message bytes, schema ID included. The tests compare them with KafkaFlow's serializer byte for byte, and each reads the other's.

## Behavior to know

- **The schema ID is in front of the message.** KafkaFlow's serializer middleware gives serializers and deserializers no message headers, so the schema ID can't go in a header. A serializer set up with `SchemaIdStrategy = Header` is rejected.
- **Caching:** each producer and consumer has its own serializer per message type. A schema is registered or looked up once per type and subject. A consumer fetches each writer schema once per schema ID.
- **Tombstones:** KafkaFlow's middleware passes a null message through without calling the serializer, so tombstones work as they do with KafkaFlow's own serializer.
