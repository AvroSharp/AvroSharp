# AWS Glue Schema Registry

[AvroSharp.Aws.Glue](https://www.nuget.org/packages/AvroSharp.Aws.Glue) writes and reads Avro messages with [AWS Glue Schema Registry](https://docs.aws.amazon.com/glue/latest/dg/schema-registry.html), for Kafka (Amazon MSK, or any broker) and Kinesis. It's fully managed and calls Glue through the AWS SDK for .NET, so it runs on every platform .NET runs on. Its own code has no trimming or Native AOT warnings; whether the AWS SDK for .NET and Confluent.Kafka work under Native AOT is up to them. AWS's own .NET package, `AWS.Glue.SchemaRegistry`, is a native build of its Java serializer for Linux only, with Apache.Avro.
- **AWS's wire format:** the header byte `0x03`, the compression byte (`0x00`, or `0x05` for zlib), the schema version's UUID, then the Avro data.
- **AWS's settings:** the registry name, auto-registration, compression, the compatibility of schemas it creates, and schema naming, with AWS's defaults.
- **Types:** generated from `.avsc` files, or your own types marked `[AvroSerializable]`, and generic records.
- **Schema evolution:** each message is read in its writer's schema and resolved to your type's.

```
dotnet add package AvroSharp.Aws.Glue --prerelease
dotnet add package AvroSharp.Aws.Glue.Kafka --prerelease   # for Confluent.Kafka
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Aws.Glue depends on AWSSDK.Glue 4. Its Confluent.Kafka serializers are in AvroSharp.Aws.Glue.Kafka, which depends on Confluent.Kafka 2, so Kinesis and other users don't take Confluent.Kafka. Both are released with AvroSharp, at the same version, and target .NET 8 and later, and .NET Standard 2.0.

On this page:
- [Use](#use)
- [Settings](#settings)
- [Moving from AWS's serializer](#moving-from-awss-serializer)
- [Behavior to know](#behavior-to-know)

## Use

```csharp
using Amazon.Glue;
using AvroSharp.Aws.Glue;
using AvroSharp.Aws.Glue.Kafka;
using Confluent.Kafka;

var glue = new AmazonGlueClient();   // the region and credentials from the environment, as usual
var options = new AvroSharpGlueOptions { RegistryName = "shop", AutoRegisterSchemas = true };

// Kafka: Order is generated from Order.avsc, or is a partial class marked [AvroSerializable].
using var producer = new ProducerBuilder<string, Order>(new ProducerConfig { BootstrapServers = "..." })
    .SetAvroSharpGlueValueSerializer(glue, options)
    .Build();

using var consumer = new ConsumerBuilder<string, Order>(new ConsumerConfig { BootstrapServers = "...", GroupId = "billing" })
    .SetAvroSharpGlueValueDeserializer(glue, options)
    .Build();
```

For Kinesis, or anything else that carries bytes, use [`AvroSharpGlueSerializer`](xref:AvroSharp.Aws.Glue.AvroSharpGlueSerializer) directly:

```csharp
var serializer = new AvroSharpGlueSerializer(glue, options);
byte[] data = await serializer.SerializeAsync(order, "orders-stream");   // the stream names the schema
Order read = await serializer.DeserializeAsync<Order>(data);
```

- **Writing:** the serializer looks up the schema version with the value's schema, once per schema, by its name and definition. With auto-registration it registers the version, or creates the schema, when the registry doesn't have it. Then it writes the header and the Avro data.
- **Reading:** the serializer fetches the writer's schema by the UUID in the message, once per version, and resolves the data to `Order`'s schema. Compressed and uncompressed messages both read.
- **Generic records:** write a `GenericRecord`, or an `AvroValue` holding one, with its own schema. Read one with `DeserializeAsync<GenericRecord>(data)`, in the writer's schema.
- **Sharing caches:** `AvroSharpGlueKafkaSerializer<T>` and `AvroSharpGlueKafkaDeserializer<T>` also take an `AvroSharpGlueSerializer`, so several of them can share one serializer's caches.

## Settings

[`AvroSharpGlueOptions`](xref:AvroSharp.Aws.Glue.AvroSharpGlueOptions) has AWS's settings, with AWS's defaults:

| Property | AWS's key | Default |
|---|---|---|
| `RegistryName` | `registry.name` | `default-registry` |
| `SchemaName` | `schemaName` | none: `SchemaNameStrategy` names the schema |
| `SchemaNameStrategy` | `schemaNameGenerationClass` | none: the schema is named after the transport (the Kafka topic, or the Kinesis stream). A strategy gets the transport name, the schema, and whether a key is written |
| `AutoRegisterSchemas` | `schemaAutoRegistrationEnabled` | `false`: the schema version must exist |
| `Compression` | `compression` | `None` (or `Zlib`) |
| `Compatibility` | `compatibility` | `BACKWARD`, for a schema the serializer creates |
| `Description`, `Tags` | `description`, `tags` | none, for a schema the serializer creates |
| `PendingVersionInterval` | | 3 seconds between checks of a new version while the registry checks its compatibility, 10 times |

## Moving from AWS's serializer

1. **Generate the types with AvroSharp.Generators.** AWS's serializer takes Apache.Avro's `ISpecificRecord` and `GenericRecord`. With `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, the generated classes implement `ISpecificRecord` too, so the same classes work with both while you move. See [the Apache.Avro compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode).
2. **Replace the serializer:**
   - `GlueSchemaRegistryKafkaSerializer` and `GlueSchemaRegistryKafkaDeserializer` → `AvroSharpGlueKafkaSerializer<T>` and `AvroSharpGlueKafkaDeserializer<T>`, from AvroSharp.Aws.Glue.Kafka;
   - the settings move from the properties file to `AvroSharpGlueOptions`;
   - the region, endpoint and credentials move to the `AmazonGlueClient`.
3. **The messages don't change:**
   - the same header and UUID byte order, pinned by a test vector from AWS's encoder;
   - the same Avro data;
   - the same registered schema text as AWS's Java serializer: Java's `Schema.toString()`. So a version it registered is found by its definition. AWS's .NET package registers Apache.Avro's text for the schema, which can differ from Java's (in key order and namespaces, for example). Glue finds a version by its exact text, so a version registered that way may not be found: with auto-registration, the serializer then registers its own text as a new version of the same schema, which Glue's compatibility check accepts.

## Behavior to know

- **Asynchronous, and synchronous for Kafka.** The AWS SDK for .NET calls Glue asynchronously only, so `AvroSharpGlueSerializer` has asynchronous methods only. The Kafka serializer and deserializer are both asynchronous and synchronous: the synchronous methods wait for Glue the first time each schema version is seen. Confluent.Kafka's consumer calls deserializers synchronously. Its producer builder takes either kind, so to pass the serializer yourself, cast it to the one you want (`(ISerializer<Order>)serializer`); `SetAvroSharpGlueValueSerializer` sets the synchronous one, so both `Produce` and `ProduceAsync` work.
- **Tombstones.** A `null` value, or a null `AvroValue`, is written as no message body, and a message without a body reads as `null`. Reading one as a value type, such as `int`, throws.
- **New versions can wait.** A version the serializer registers is `PENDING` while Glue checks its compatibility. The serializer checks it every `PendingVersionInterval`, 10 times at most. It throws an `InvalidOperationException` if the check fails, and a `TimeoutException` if the version is still pending.
- **Not yet checked against AWS's own package.** The format comes from AWS's Java source, and the tests pin it with a test vector. Messages from AWS's own .NET package haven't been read in a test yet: its native library didn't start in the Docker environment used for this package.
