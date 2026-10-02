# AvroSharp.Aws.Glue

A managed [AWS Glue Schema Registry](https://docs.aws.amazon.com/glue/latest/dg/schema-registry.html) serializer on [AvroSharp](https://github.com/AvroSharp/AvroSharp), for Kafka (Amazon MSK, or any broker) and Kinesis. It's an alternative to AWS's `AWS.Glue.SchemaRegistry`, which is a native build for Linux only, about 119 MB, with Apache.Avro. This package:
- **runs everywhere:** it's fully managed, calls Glue through the AWS SDK for .NET, and runs on every platform .NET does;
- **writes AWS's wire format:** the header byte `0x03`, the compression byte (none, or zlib), the schema version's UUID, then the Avro data;
- **uses AWS's settings and defaults:** the registry name, auto-registration, compression, compatibility, and schema naming after the topic or stream;
- **reads generated and `[AvroSerializable]` types, and generic records,** each message in its writer's schema, resolved to your type's;
- **includes Confluent.Kafka serializers.**

> **Status:** new in the 1.0.0 release candidates, and released with AvroSharp at the same version. Its API may still change until 1.0.0; from then it follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.html) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.Aws.Glue --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Aws.Glue depends on AWSSDK.Glue 4 and Confluent.Kafka 2, and targets .NET 8 and later, and .NET Standard 2.0.

## Use

```csharp
var glue = new AmazonGlueClient();
var options = new AvroSharpGlueOptions { RegistryName = "shop", AutoRegisterSchemas = true };

using var producer = new ProducerBuilder<string, Order>(producerConfig)
    .SetAvroSharpGlueValueSerializer(glue, options)
    .Build();

// Or, for Kinesis and anything else that carries bytes:
var serializer = new AvroSharpGlueSerializer(glue, options);
byte[] data = await serializer.SerializeAsync(order, "orders-stream");
Order read = await serializer.DeserializeAsync<Order>(data);
```

The [guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html) covers the settings, moving from AWS's serializer, and the behavior to know.
