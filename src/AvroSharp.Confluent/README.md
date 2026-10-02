![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/logo.png)

# AvroSharp.Confluent

Confluent Schema Registry serializers and deserializers for [Confluent.Kafka](https://github.com/confluentinc/confluent-kafka-dotnet), built on [AvroSharp](https://github.com/AvroSharp/AvroSharp), without Apache.Avro.

- **Types:**
  - types generated from `.avsc` files;
  - your own C# types marked `[AvroSerializable]`;
  - generic records (`AvroValue`);
  - the primitives `string`, `int`, `long`, `float`, `double`, `bool` and `byte[]`, as in Confluent's serializers (not yet its `Null`).
- **Confluent's own code** for the registry: AvroSharp's serializers derive from Confluent's serializer base classes, so these work as in Confluent's serializer:
  - subject name strategies;
  - registration and `use.latest.version`;
  - schema ID strategies (prefix or header);
  - schema references;
  - data contract rules: domain rules (such as CEL, which names the Avro fields, as Java's serializer does) and encoding rules. Field rules, such as field-level encryption (CSFLE), and migration rules aren't supported yet, and fail rather than being skipped.

  Only the Avro encoding is AvroSharp's. With `use.latest.version` or `use.schema.id`, the serializer also checks that the target schema encodes as your type's, logical types included, which Confluent's doesn't.
- **Interchangeable with Confluent's serializer:**
  - the same message bytes for the same schema and value;
  - the same configuration keys (`avro.serializer.*`, `avro.deserializer.*`);
  - each reads what the other writes. The tests compare them byte for byte.
- **Schema evolution:** a message is read in its writer's schema, from the registry by its schema ID, and resolved to your type's schema, so older and newer versions both read.

> **Status:** new in 1.0.0, and released with AvroSharp at the same version. It follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/confluent.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Confluent.html) · [Sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/Confluent) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.Confluent --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Generators turns your `.avsc` files and `[AvroSerializable]` types into C# code while the project builds (see [code generation](https://avrosharp.github.io/AvroSharp/docs/code-generation.html)). AvroSharp.Confluent depends on Confluent.SchemaRegistry 2.14.0 or later (below 3.0). The tests run against 2.14.0 and the newest release.

## Use

```csharp
using AvroSharp.Confluent;
using Confluent.Kafka;
using Confluent.SchemaRegistry;

using var registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = "http://localhost:8081" });

// Order is generated from Order.avsc, or is a partial class marked [AvroSerializable].
using var producer = new ProducerBuilder<string, Order>(new ProducerConfig { BootstrapServers = "localhost:9092" })
    .SetAvroSharpKeySerializer(registry)
    .SetAvroSharpValueSerializer(registry)
    .Build();
await producer.ProduceAsync("orders", new Message<string, Order> { Key = order.Id.ToString(), Value = order });

using var consumer = new ConsumerBuilder<string, Order>(new ConsumerConfig { BootstrapServers = "localhost:9092", GroupId = "billing" })
    .SetAvroSharpKeyDeserializer(registry)
    .SetAvroSharpValueDeserializer(registry)
    .Build();
```

The [guide](https://avrosharp.github.io/AvroSharp/docs/confluent.html) has:
- generic records;
- .NET Standard;
- the settings and their Confluent keys;
- moving from Confluent's Avro serializer and from Chr.Avro;
- tombstones and the other behaviors to know;
- which data contract rules run.
