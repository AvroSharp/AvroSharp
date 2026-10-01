# Confluent Schema Registry and Kafka

[AvroSharp.Confluent](https://www.nuget.org/packages/AvroSharp.Confluent) gives Confluent.Kafka producers and consumers Avro serializers on AvroSharp, with Confluent Schema Registry. The same serializers work with registries that have its API, such as Redpanda, Karapace and Apicurio. It's a drop-in replacement for Confluent's own Avro serializer: no Apache.Avro, the same configuration keys and the same message bytes. For the registry, it uses Confluent's own code, through Confluent's serializer base classes:
- subject name strategies;
- registration;
- schema ID strategies;
- references;
- rules.

On this page:
- [Use](#use)
- [Settings](#settings)
- [Moving from Confluent's Avro serializer](#moving-from-confluents-avro-serializer)
- [Moving from Chr.Avro](#moving-from-chravro)
- [Behavior to know](#behavior-to-know)
- [Data contract rules](#data-contract-rules)

The [Confluent sample](../samples/Confluent/Program.cs) produces and consumes through a real broker and registry. It reads the same messages three ways: as the type that wrote them, as generic values, and as a newer version of the type. Its [compose file](https://github.com/AvroSharp/AvroSharp/blob/main/samples/Confluent/compose.yaml) starts Redpanda: run `docker compose up -d --wait`, then `dotnet run`, in `samples/Confluent`.

```
dotnet add package AvroSharp.Confluent --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Confluent depends on Confluent.SchemaRegistry 2.14.0 or later (below 3.0), and is released with AvroSharp, at the same version.

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

The serializers can also be created directly:
- [`new AvroSharpSerializer<Order>(registry, config)`](xref:AvroSharp.Confluent.AvroSharpSerializer`1) and [`new AvroSharpDeserializer<Order>(registry, config)`](xref:AvroSharp.Confluent.AvroSharpDeserializer`1);
- the deserializer is asynchronous, as Confluent's is: wrap it with `.AsSyncOverAsync()` for a consumer.

[`AvroSharpSerdeExtensions`](xref:AvroSharp.Confluent.AvroSharpSerdeExtensions) does that for you.

**Generic records.** For code that has schemas rather than types, [`AvroSharpGeneric`](xref:AvroSharp.Confluent.AvroSharpGeneric):

```csharp
var serializer = AvroSharpGeneric.CreateSerializer(registry, schema);         // writes AvroValue values of the schema
var deserializer = AvroSharpGeneric.CreateDeserializer(registry);             // each message as its writer's schema
var asV2 = AvroSharpGeneric.CreateDeserializer(registry, readerSchema: v2);   // or resolved to one schema
```

**.NET Standard and .NET Framework.** On .NET 5 and later, a generated type registers itself in [`AvroTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroTypes.html) when its assembly loads, and the constructors above find it. On other targets, either:
- call `AvroTypes.Register(Order.AvroTypeInfo)` once at startup;
- or pass the type's info: `new AvroSharpSerializer<Order>(registry, Order.AvroTypeInfo)`.

## Settings

[`AvroSharpSerializerConfig`](xref:AvroSharp.Confluent.AvroSharpSerializerConfig) and [`AvroSharpDeserializerConfig`](xref:AvroSharp.Confluent.AvroSharpDeserializerConfig) take the keys of Confluent's `AvroSerializerConfig` and `AvroDeserializerConfig`. They can be built from a configuration section of key-value pairs.

| Serializer | Key | Default |
|---|---|---|
| `AutoRegisterSchemas` | `avro.serializer.auto.register.schemas` | `true` |
| `NormalizeSchemas` | `avro.serializer.normalize.schemas` | `false` |
| `UseSchemaId` | `avro.serializer.use.schema.id` | none |
| `UseLatestVersion` | `avro.serializer.use.latest.version` | `false`; can't be combined with auto-registration |
| `UseLatestWithMetadata` | `avro.serializer.use.latest.with.metadata` | none |
| `SubjectNameStrategy` | `avro.serializer.subject.name.strategy` | `Associated`: the topic's association, or else `Topic` |
| `SchemaIdStrategy` | `avro.serializer.schema.id.strategy` | `Prefix` |
| `BufferBytes` | `avro.serializer.buffer.bytes` | 1024 |

| Deserializer | Key | Default |
|---|---|---|
| `UseLatestVersion` | `avro.deserializer.use.latest.version` | `false` |
| `UseLatestWithMetadata` | `avro.deserializer.use.latest.with.metadata` | none |
| `SubjectNameStrategy` | `avro.deserializer.subject.name.strategy` | `Associated` |
| `SchemaIdStrategy` | `avro.deserializer.schema.id.strategy` | `Dual`: a header, or else the prefix |

## Moving from Confluent's Avro serializer

Confluent.SchemaRegistry.Serdes.Avro uses Apache.Avro and its `avrogen` classes.

1. **Generate the types with AvroSharp.Generators.**
   - **Moving gradually?** Set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, and the generated classes also implement Apache's `ISpecificRecord`. The same class then works with both serializers while you move one producer or consumer at a time. This repository's tests use such classes, and [the Apache.Avro compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode) has the details.
   - **Moving at once?** Use AvroSharp's own types, without Apache.Avro.
2. **Replace the serializers:**
   - `AvroSerializer<T>` → `AvroSharpSerializer<T>`;
   - `AvroDeserializer<T>` → `AvroSharpDeserializer<T>`;
   - `AvroSerializerConfig` → `AvroSharpSerializerConfig`, with the same keys.
3. **Nothing changes on the wire or in the registry:**
   - the same subjects;
   - the same message bytes, schema ID included. The schema's JSON may be formatted differently, but registries treat equivalent schemas as one;
   - a top-level `bytes` value is still the message body itself, as Confluent's serializers (.NET and Java) write it.

## Moving from Chr.Avro

Chr.Avro.Confluent maps your classes to schemas by reflection when the serializer is built. AvroSharp writes that code at compile time instead:

- **Your types:** add `[AvroSerializable]`, and `partial`, to the classes you serialize, or generate them from the subject's `.avsc` files.
- **Builder methods:**
  - `SetAvroValueSerializer(registry, AutomaticRegistrationBehavior.Always)` → `SetAvroSharpValueSerializer(registry)`;
  - `SetAvroValueDeserializer(registry)` → `SetAvroSharpValueDeserializer(registry)`.
- **Registration:** Chr.Avro doesn't register schemas by default, but AvroSharp does, as Confluent's serializer does. To keep Chr.Avro's behavior, set `AutoRegisterSchemas = false`, together with `UseLatestVersion = true` if you wrote with the subject's latest schema.
- **Names:** Chr.Avro matches a schema's fields to your members loosely. AvroSharp names fields after your members, as written:
  - set `[AvroName]` on a member whose field is named differently;
  - or set `[assembly: AvroSerializableDefaults(FieldNames = AvroNaming.CamelCase)]` for camelCase fields.

## Behavior to know

- **Tombstones.** A `null` value, or a null `AvroValue`, is written as a message with no body, which Kafka calls a tombstone. Reading one gives `null`, or a null `AvroValue`. A value type such as `int` can't be null, so reading a tombstone into one throws, as in Confluent's deserializer.
- **`use.latest.version`, `use.latest.with.metadata` and `use.schema.id`.** The message carries that schema's ID, but its bytes are written in your type's schema. So the serializer checks once that the two schemas encode alike (the same Parsing Canonical Form), and throws when they don't. Otherwise readers would decode the message wrongly.
- **Configuration keys.** A key the serializer doesn't know is an error, as in Confluent's serializers, so a misspelled key isn't ignored. Keys under `rules.` and `subject.name.strategy.` are passed through to Confluent's code.
- **The `Record` and `TopicRecord` strategies** name the subject after a record schema, as Confluent's .NET and Java serializers do. Any other schema, such as a primitive, has no record name, and serializing it fails with an error that says so.

## Data contract rules

Rules run in Confluent's executors (Confluent.SchemaRegistry.Rules and Confluent.SchemaRegistry.Encryption), which you register as with Confluent's serializer.

| Rule | Generated or `[AvroSerializable]` types | Generic values (`AvroValue`) |
|---|---|---|
| CEL conditions (`CEL`) | Run. With the Apache.Avro compatibility mode, expressions name the Avro fields. Otherwise they name the C# properties, such as `message.Name`: the Avro field names, unless `[AvroName]` or a naming setting changes them. CEL transforms are untested. | The rule fails, so the message isn't written. |
| Payload encryption (`ENCRYPT_PAYLOAD`) | Runs | Runs |
| Field rules: field-level encryption (`ENCRYPT`, CSFLE) and `CEL_FIELD` | Not supported yet: they fail with `NotSupportedException` instead of leaving the fields unchanged. | Not supported yet |
| Migration rules | Not supported yet: a deserializer that would have to run them fails with `NotSupportedException`. | Not supported yet |
