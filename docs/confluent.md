# Confluent Schema Registry and Kafka

[AvroSharp.Confluent](https://www.nuget.org/packages/AvroSharp.Confluent) gives Confluent.Kafka producers and consumers Avro serializers on AvroSharp, with Confluent Schema Registry. The same serializers work with registries that have its API, such as Redpanda, Karapace and Apicurio. It replaces Confluent's own Avro serializer with no Apache.Avro, the same configuration keys and the same message bytes; the differences are listed under [Moving from Confluent's Avro serializer](#moving-from-confluents-avro-serializer). For the registry, it uses Confluent's own code, through Confluent's serializer base classes:
- subject name strategies;
- registration;
- schema ID strategies;
- references;
- rules.

For KafkaFlow, [AvroSharp.KafkaFlow](kafkaflow.md) puts these serializers in KafkaFlow's middleware.

On this page:
- [Use](#use)
- [Settings](#settings)
- [Moving from Confluent's Avro serializer](#moving-from-confluents-avro-serializer)
- [Moving from Chr.Avro](#moving-from-chravro)
- [Behavior to know](#behavior-to-know)
- [Metrics and tracing](#metrics-and-tracing)
- [Performance](#performance)
- [Data contract rules](#data-contract-rules)

The [Confluent sample](../samples/Confluent/Program.cs) produces and consumes through a real broker and registry. It reads the same messages three ways: as the type that wrote them, as generic values, and as a newer version of the type. Its [compose file](https://github.com/AvroSharp/AvroSharp/blob/main/samples/Confluent/compose.yaml) starts Redpanda: run `docker compose up -d --wait`, then `dotnet run`, in `samples/Confluent`.

```
dotnet add package AvroSharp.Confluent
dotnet add package AvroSharp.Generators
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

The serializers can also be created directly: [`new AvroSharpSerializer<Order>(registry, config)`](xref:AvroSharp.Confluent.AvroSharpSerializer`1) and [`new AvroSharpDeserializer<Order>(registry, config)`](xref:AvroSharp.Confluent.AvroSharpDeserializer`1).
- **Both synchronous and asynchronous.** Each implements Confluent.Kafka's synchronous interface (`ISerializer<T>`, `IDeserializer<T>`) as well as the asynchronous one, so `producer.Produce` works, and a consumer takes the deserializer as it is.
- **The producer builder has a `SetValueSerializer` for each interface,** so pass the serializer as one of them, such as `(ISerializer<Order>)serializer`. [`AvroSharpSerdeExtensions`](xref:AvroSharp.Confluent.AvroSharpSerdeExtensions) sets the synchronous ones, which serve `ProduceAsync` too.
- **No task once the schema is known.** The synchronous path waits for the registry the first time a topic's schema is written or a schema ID is read; after that it reads and writes without a task. Settings that can run rules asynchronously (`use.latest.version`, `use.latest.with.metadata`, `use.schema.id`, and schemas with rules) wait on every message.

**Generic records.** For code that has schemas rather than types, [`AvroSharpGeneric`](xref:AvroSharp.Confluent.AvroSharpGeneric):

```csharp
var serializer = AvroSharpGeneric.CreateSerializer(registry, schema);         // writes AvroValue values of the schema
var records = AvroSharpGeneric.CreateSerializer(registry);                    // writes each record with its own schema
var deserializer = AvroSharpGeneric.CreateDeserializer(registry);             // each message as its writer's schema
var asV2 = AvroSharpGeneric.CreateDeserializer(registry, readerSchema: v2);   // or resolved to one schema
```

On Confluent.Kafka's builders, `SetAvroSharpGenericValueSerializer` and `SetAvroSharpGenericValueDeserializer` set them, with or without a schema.

**.NET Standard and .NET Framework.** On .NET 5 and later, a generated type registers itself in [`AvroTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroTypes.html) when its assembly loads, and the constructors above find it. On other targets, either:
- call `AvroTypes.Register(Order.AvroTypeInfo)` once at startup;
- or pass the type's info: `new AvroSharpSerializer<Order>(registry, Order.AvroTypeInfo)`.

## Settings

[`AvroSharpSerializerConfig`](xref:AvroSharp.Confluent.AvroSharpSerializerConfig) and [`AvroSharpDeserializerConfig`](xref:AvroSharp.Confluent.AvroSharpDeserializerConfig) take the keys of Confluent's `AvroSerializerConfig` and `AvroDeserializerConfig`. They can be built from key-value pairs, which they copy, such as a dictionary or a configuration section:

```csharp
// appsettings.json: { "Kafka": { "Serializer": { "avro.serializer.auto.register.schemas": "false" } } }
var config = new AvroSharpSerializerConfig(configuration.GetSection("Kafka:Serializer").AsEnumerable(makePathsRelative: true)!);
```

A section's own entry has no value, and is left out.

| Serializer | Key | Default |
|---|---|---|
| `AutoRegisterSchemas` | `avro.serializer.auto.register.schemas` | `true` |
| `NormalizeSchemas` | `avro.serializer.normalize.schemas` | `false` |
| `UseSchemaId` | `avro.serializer.use.schema.id` | none |
| `UseSchemaGuid` | `avro.serializer.use.schema.guid` | none; Java's `use.schema.guid` |
| `UseLatestVersion` | `avro.serializer.use.latest.version` | `false`; can't be combined with auto-registration |
| `UseLatestWithMetadata` | `avro.serializer.use.latest.with.metadata` | none |
| `SubjectNameStrategy` | `avro.serializer.subject.name.strategy` | `Associated`: the topic's association, or else `Topic` |
| `SchemaIdStrategy` | `avro.serializer.schema.id.strategy` | `Prefix` |
| `BufferBytes` | `avro.serializer.buffer.bytes` | 1024 |
| `RemoveJavaProperties` | `avro.serializer.remove.java.properties` | `false`; Java's `avro.remove.java.properties` |
| `TombstoneBehavior` | `avro.serializer.tombstone.behavior` | `None`, Confluent's behavior; AvroSharp's own key |
| `LatestCompatibilityStrict` | `avro.serializer.latest.compatibility.strict` | `true`; Java's `latest.compatibility.strict` |
| `IdCompatibilityStrict` | `avro.serializer.id.compatibility.strict` | `true`; Java's `id.compatibility.strict` |

| Deserializer | Key | Default |
|---|---|---|
| `UseLatestVersion` | `avro.deserializer.use.latest.version` | `false` |
| `UseLatestWithMetadata` | `avro.deserializer.use.latest.with.metadata` | none |
| `SubjectNameStrategy` | `avro.deserializer.subject.name.strategy` | `Associated` |
| `SchemaIdStrategy` | `avro.deserializer.schema.id.strategy` | `Dual`: a header, or else the prefix |
| `FailOnTrailingData` | `avro.deserializer.fail.on.trailing.data` | `false`; Java's `avro.fail.on.trailing.data` |
| `TombstoneBehavior` | `avro.deserializer.tombstone.behavior` | `None`, Confluent's behavior; AvroSharp's own key |

Two keys come from Java's serializers, which Confluent's .NET serializer doesn't have:
- **`RemoveJavaProperties`** removes the property `avro.java.string` from the schema before it is registered or looked up, as Java's serializer does. Java's generated classes add it, so with the option on everywhere, the same `.avsc` registers as one version from Java and .NET.
- **`FailOnTrailingData`** fails a message that has bytes left after the value, a sign of a wrong schema or a corrupt message. Without it, they are ignored, as Confluent's .NET deserializer does.

**Subjects and pinned schemas.** For subject names that `SubjectNameStrategy` can't express, such as a schema context or a team's prefix, give the serializer or deserializer a function:

```csharp
var serializer = new AvroSharpSerializer<Order>(registry)
{
    CustomSubjectNameStrategy = (context, recordType) => Task.FromResult($":.billing:{context.Topic}-value"),
};
```

To write with one version of a subject, whatever the topic, pin it. The registry is asked for the version's schema ID once:

```csharp
var builder = await new ProducerBuilder<string, Order>(producerConfig)
    .SetAvroSharpValueSerializerAsync(registry, subject: "orders-value", version: 3);
```

`UseSchemaId` pins a schema by its ID, and `UseSchemaGuid` by its GUID, under the subject the strategy names. A pinned schema that differs from your type's is written as described under [behavior to know](#behavior-to-know), with `IdCompatibilityStrict`.

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
   - a top-level `bytes` value is still the message body itself, as Confluent's serializers (.NET and Java) write it. Bytes with a logical type, such as a decimal, keep Avro's length prefix, as Java's serializer writes a `BigDecimal`.
4. **What differs:**
   - **Writing with another schema:** with `use.latest.version`, `use.latest.with.metadata` or `use.schema.id`, values are written in that schema, as Java writes them, where Confluent's specific serializer writes the type's own bytes under its ID ([behavior to know](#behavior-to-know)).
   - **Generic records:** `AvroSharpGeneric.CreateSerializer(registry)` writes each record with its own schema, as Confluent's generic serializer does; it writes records only. With a schema, it writes values of that schema, primitives included.
   - **Synchronous too:** both serializers also implement Confluent.Kafka's synchronous interfaces, which Confluent's don't. So passing one straight to `SetValueSerializer` needs a cast; the `SetAvroSharp…` extensions don't.

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

- **Native AOT.** CI publishes an application that uses it with Native AOT and runs it ([the add-ons smoke test](https://github.com/AvroSharp/AvroSharp/tree/main/tests/AvroSharp.AotSmoke.Addons/Program.cs)). AvroSharp.Confluent has no trim or AOT warnings. Confluent's `CachedSchemaRegistryClient` reads the registry's REST API with Newtonsoft.Json, which has trim and AOT warnings of its own; they come from Confluent's client, not from the serializers.
- **Tombstones.** A `null` value, or a null `AvroValue`, is written as a message with no body, which Kafka calls a tombstone. Reading one gives `null`, or a null `AvroValue`. A value type such as `int` can't be null, so reading a tombstone into one throws, as in Confluent's deserializer.
  - **Confluent.Kafka's `Null`** has the schema `"null"`, as in Confluent's serializer: `AvroSharpSerializer<Null>` writes a message with the schema ID and an empty body, not a tombstone.
  - **`TombstoneBehavior.Strict`**, as in Chr.Avro, makes sure a tombstone can't be mistaken for a value. The serializer or deserializer is refused when it's created if the type can't be null (a value type, rather than `int?`), or if the schema can hold null (`"null"`, or a union with `"null"`). The deserializer checks each writer's schema as it meets it. A key with no value throws, since keys have no tombstones.
- **`use.latest.version`, `use.latest.with.metadata` and `use.schema.id`.** The message carries that schema's ID, so it is written in that schema, as Java's serializer and Confluent's generic serializer write it. Confluent's specific serializer doesn't: it writes your type's bytes under the other ID. This is decided once per schema ID:
  - When the two schemas encode alike (the same Parsing Canonical Form and logical types), your type's bytes are written as they are.
  - Otherwise the value is resolved into that schema, as a reader of your type's data would read it, and written in it. Fields the schema lacks are dropped, and fields it adds get their defaults. This costs a second encoding per message.
  - With `LatestCompatibilityStrict` (or `IdCompatibilityStrict`, for `use.schema.id`), the default, that schema must be able to read your type's, by [the compatibility check](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaCompatibility.html); the error names every reason it can't. Off, values are written whenever the schemas resolve, and a value the schema can't hold, such as a union branch it lacks, fails when it is written.
  - Other logical types, or another decimal precision or scale, are always refused: the encoded numbers would mean other values.
  - A top-level `bytes` value is the message body alone, so it is written only with a schema that encodes alike.
- **Configuration keys.** A key the serializer doesn't know is an error, as in Confluent's serializers, so a misspelled key isn't ignored. Keys under `rules.` and `subject.name.strategy.` are passed through to Confluent's code.
- **The `None` subject name strategy isn't supported,** with or without `use.schema.id`: Confluent's client needs a subject to look a schema ID up in, and Confluent's serializer fails the same way. The serializer says so.
- **The `Record` and `TopicRecord` strategies** name the subject after a record schema, as Confluent's .NET and Java serializers do. Any other schema, such as a primitive, has no record name, and serializing it fails with an error that says so.

## Metrics and tracing

The serializers publish metrics through a `Meter`, and the registry calls as activities through an `ActivitySource`. Both are named `AvroSharp.Confluent`. Nothing is measured unless a listener subscribes, so they cost nothing otherwise. With OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("AvroSharp.Confluent"))
    .WithTracing(tracing => tracing.AddSource("AvroSharp.Confluent"));
```

| Instrument | Type | Tags |
|---|---|---|
| `avrosharp.confluent.serde.duration` (s) | histogram: the time to write or read a message, registry calls included | `avrosharp.serde` (`serializer`, `deserializer`), `messaging.destination.name` (the topic), `avrosharp.component` (`key`, `value`), `error.type` when it failed |
| `avrosharp.confluent.serde.errors` | counter: messages that failed | as above |
| `avrosharp.confluent.rule.failures` | counter: messages whose data contract rules failed (a `RuleException`), among the errors | as above |
| `avrosharp.confluent.registry.calls` | counter: calls to the registry client when the serializer's own caches miss; Confluent's client may still answer from its cache | `avrosharp.operation` (`register`, `lookup`, `get_schema`, `migrations`), `avrosharp.subject` |

Each registry call is also an activity, `schema_registry <operation>`, with the same tags.

## Performance

After the first message of a topic or schema ID, the serializers don't go back to the registry, and they skip Confluent's per-message lookups. A message writes into a buffer the thread keeps, so a write allocates only the message, and a read only the value (plus the task, on the asynchronous path). With `use.latest.version`, the latest schema is still asked for on every message, as Confluent's serde does, but the writer's schema and the check for migrations are looked up once.

`bench/AvroSharp.Confluent.Benchmarks` compares AvroSharp.Confluent with Confluent's Avro serializer and Chr.Avro.Confluent on an in-memory registry: `dotnet run -c Release -f net10.0 --project bench/AvroSharp.Confluent.Benchmarks`. The results are in [benchmarks](benchmarks.md#confluent-schema-registry-serializers).

## Data contract rules

Rules run in Confluent's executors (Confluent.SchemaRegistry.Rules and Confluent.SchemaRegistry.Encryption), which you register as with Confluent's serializer.

| Rule | Generated or `[AvroSerializable]` types | Generic values (`AvroValue`) |
|---|---|---|
| CEL conditions and transforms (`CEL`) | Run, by the Avro field names | Run, by the Avro field names |
| Payload encryption (`ENCRYPT_PAYLOAD`) | Runs | Runs |
| Field rules: field-level encryption (`ENCRYPT`, CSFLE) and `CEL_FIELD` | Run, on the tagged fields | Run, on the tagged fields |
| Migration rules (`UPGRADE`, `DOWNGRADE`, such as `JSONATA`) | Run, on Avro JSON, then read as the type | Run, on Avro JSON |

**CEL names the Avro fields,** as rules written for Java's and Confluent's serializers do: `message.customer_name`, not the C# property `message.CustomerName`. Confluent's CEL executor reads a value by its Avro field names only as one of Apache.Avro's records. So when a subject's domain rules include CEL rules, the rules get the value decoded from its encoding as Apache.Avro's generic model: a `GenericRecord`, with `GenericEnum`, lists and dictionaries inside, as Confluent's generic serializer gives them.
- **Apache.Avro comes with Confluent.SchemaRegistry.Rules,** which has the CEL executor. AvroSharp.Confluent doesn't reference it; it finds it at run time.
- **A condition that passes** writes the value's own encoding, so the bytes are the same as without the rule.
- **A transform** returns the value to write (or, on reads, to read): a record of the subject's schema in Apache.Avro's generic model. A rule that returns anything else, such as a CEL map literal, fails with `InvalidOperationException`.
- **Other domain rules,** with no CEL rule among them, get your value itself.
**Field rules** run as in Confluent's serde, on the fields whose tags the rule names (every field, for a rule without tags): a field's tags are its `confluent:tags` property and the schema metadata's tags for its path.
- **Fields are found by walking the value in the rules' schema,** the latest one (or the writer's, on reads): records, arrays, maps and union branches, as Confluent's `AvroUtils` walks them. Messages written with field-level encryption by Confluent's .NET serde read here, and it reads what AvroSharp writes (tested with the same registry and keys).
- **The transform gets the field's .NET value:** a `string`, a `byte[]` for bytes and fixed, a number, a `bool`, an enum's symbol, or `null`. Field-level encryption takes strings and bytes, as in Confluent's serde.
- **`CEL_FIELD`** expressions can use `value`, `name`, `fullName`, `typeName` and `tags`. Its `message` variable, the record that holds the field, isn't readable by CEL here.
- **Without field rules,** nothing is walked.

**Migration rules** run as in Confluent's and Java's deserializers, with `use.latest.version` or `use.latest.with.metadata`, between the writer's version and that one: `UPGRADE` rules to a newer version, `DOWNGRADE` rules to an older one.
- **The rules see the value as Avro JSON,** in the shape of Apache.Avro's `JsonEncoder`: unions wrapped by their branch's full name (`{"string": "x"}`), bytes and fixed as ISO-8859-1 strings, enums as their symbols, and logical types as their underlying values. Expressions written for Confluent's or Java's serializers work unchanged.
- **What they return** is read as the target version's schema, so it must be a value of that schema; a generated type then reads it as its own schema.
- **The rules are looked up once** per writer version and target version; the migration itself runs on each message.
- **A top-level `bytes` schema** isn't migrated: its message is the bytes alone.

- **Trimming and Native AOT:** Confluent.SchemaRegistry.Rules and Apache.Avro use reflection and aren't marked trim-compatible, and CEL rules aren't tested under Native AOT. Without CEL rules, none of this is used.
