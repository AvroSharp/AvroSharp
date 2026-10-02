![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/logo.png)

# AvroSharp.Aws.Glue

A managed [AWS Glue Schema Registry](https://docs.aws.amazon.com/glue/latest/dg/schema-registry.html) serializer on [AvroSharp](https://github.com/AvroSharp/AvroSharp), for Kafka (Amazon MSK, or any broker) and Kinesis. It's an alternative to AWS's `AWS.Glue.SchemaRegistry`, which is a native build for Linux only, about 119 MB, with Apache.Avro. This package:
- **runs everywhere:** it's fully managed, calls Glue through the AWS SDK for .NET, and runs on every platform .NET does;
- **writes AWS's wire format:** the header byte `0x03`, the compression byte (none, or zlib), the schema version's UUID, then the Avro data;
- **uses AWS's settings and defaults:** the registry name, auto-registration, compression, compatibility, and schema naming after the topic or stream;
- **reads generated and `[AvroSerializable]` types, and generic records,** each message in its writer's schema, resolved to your type's;
- **has Confluent.Kafka serializers** in a package of their own, [AvroSharp.Aws.Glue.Kafka](https://www.nuget.org/packages/AvroSharp.Aws.Glue.Kafka), so Kinesis and other users don't take Confluent.Kafka.

> **Status:** new in 1.0.0, and released with AvroSharp at the same version. It follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.html) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.Aws.Glue --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

For Kafka, add `AvroSharp.Aws.Glue.Kafka` too. AvroSharp.Aws.Glue depends on AWSSDK.Glue 4, and targets .NET 8 and later, and .NET Standard 2.0.

## Use

```csharp
var glue = new AmazonGlueClient();
var options = new AvroSharpGlueOptions { RegistryName = "shop", AutoRegisterSchemas = true };

// Kinesis, or anything else that carries bytes:
var serializer = new AvroSharpGlueSerializer(glue, options);
byte[] data = await serializer.SerializeAsync(order, "orders-stream");
Order read = await serializer.DeserializeAsync<Order>(data);

// Kafka, with AvroSharp.Aws.Glue.Kafka:
using var producer = new ProducerBuilder<string, Order>(producerConfig)
    .SetAvroSharpGlueValueSerializer(glue, options)
    .Build();
```

The [guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html) covers the settings, moving from AWS's serializer, and the behavior to know.
