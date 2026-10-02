# AWS Glue Schema Registry

[AvroSharp.Aws.Glue](https://www.nuget.org/packages/AvroSharp.Aws.Glue) writes and reads Avro messages with [AWS Glue Schema Registry](https://docs.aws.amazon.com/glue/latest/dg/schema-registry.html), for Kafka (Amazon MSK, or any broker) and Kinesis. It's fully managed and calls Glue through the AWS SDK for .NET, so it runs on every platform .NET runs on, and its own code is Native AOT compatible. AWS's own .NET package, `AWS.Glue.SchemaRegistry`, is a native build of its Java serializer for Linux only, with Apache.Avro.
- **AWS's wire format:** the header byte `0x03`, the compression byte (`0x00`, or `0x05` for zlib), the schema version's UUID, then the Avro data.
- **AWS's settings:** the registry name, auto-registration, compression, the compatibility of schemas it creates, and schema naming, with AWS's defaults.
- **Types:** generated from `.avsc` files, or your own types marked `[AvroSerializable]`, and generic records.
- **Schema evolution:** each message is read in its writer's schema and resolved to your type's.

```
dotnet add package AvroSharp.Aws.Glue --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Aws.Glue depends on AWSSDK.Glue 4 and Confluent.Kafka 2, and is released with AvroSharp, at the same version. It targets .NET 8 and later, and .NET Standard 2.0.

On this page:
- [Use](#use)
- [Settings](#settings)
- [Moving from AWS's serializer](#moving-from-awss-serializer)
- [Behavior to know](#behavior-to-know)

## Use

```csharp
using Amazon.Glue;
using AvroSharp.Aws.Glue;
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
| `SchemaNameStrategy` | `schemaNameGenerationClass` | none: the schema is named after the transport (the Kafka topic, or the Kinesis stream) |
| `AutoRegisterSchemas` | `schemaAutoRegistrationEnabled` | `false`: the schema version must exist |
| `Compression` | `compression` | `None` (or `Zlib`) |
| `Compatibility` | `compatibility` | `BACKWARD`, for a schema the serializer creates |
| `Description`, `Tags` | `description`, `tags` | none, for a schema the serializer creates |
| `PendingVersionInterval` | | 3 seconds between checks of a new version while the registry checks its compatibility, 10 times |

## Moving from AWS's serializer

1. **Generate the types with AvroSharp.Generators.** AWS's serializer takes Apache.Avro's `ISpecificRecord` and `GenericRecord`. With `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, the generated classes implement `ISpecificRecord` too, so the same classes work with both while you move. See [the Apache.Avro compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode).
2. **Replace the serializer:**
   - `GlueSchemaRegistryKafkaSerializer` and `GlueSchemaRegistryKafkaDeserializer` → `AvroSharpGlueKafkaSerializer<T>` and `AvroSharpGlueKafkaDeserializer<T>`;
   - the settings move from the properties file to `AvroSharpGlueOptions`;
   - the region, endpoint and credentials move to the `AmazonGlueClient`.
3. **The messages don't change:**
   - the same header and UUID byte order, pinned by a test vector from AWS's encoder;
   - the same Avro data;
   - the same registered schema text: Java's `Schema.toString()`, which AWS's Java and native serializers register too. So a version they registered is found by its definition.

## Behavior to know

- **Asynchronous only.** The AWS SDK for .NET calls Glue asynchronously, so the serializer has no synchronous methods. Confluent.Kafka's consumer calls deserializers synchronously, so `SetAvroSharpGlueValueDeserializer` wraps the deserializer, and it waits for Glue the first time each schema version is seen.
- **New versions can wait.** A version the serializer registers is `PENDING` while Glue checks its compatibility. The serializer waits until it's `AVAILABLE`, and throws if it fails or doesn't become available in time.
- **Not yet checked against AWS's own package.** The format comes from AWS's Java source, and the tests pin it with a test vector. Messages from AWS's own .NET package haven't been read in a test yet: its native library didn't start in the Docker environment used for this package.
