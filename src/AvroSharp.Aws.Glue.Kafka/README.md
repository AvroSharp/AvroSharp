![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/logo.png)

# AvroSharp.Aws.Glue.Kafka

[Confluent.Kafka](https://github.com/confluentinc/confluent-kafka-dotnet) serializers and deserializers for [AWS Glue Schema Registry](https://docs.aws.amazon.com/glue/latest/dg/schema-registry.html), on [AvroSharp.Aws.Glue](https://www.nuget.org/packages/AvroSharp.Aws.Glue), for Amazon MSK or any Kafka broker. They stand in for AWS's `GlueSchemaRegistryKafkaSerializer` and `GlueSchemaRegistryKafkaDeserializer`, in AWS's wire format, fully managed, without Apache.Avro or a native library. Each is both asynchronous and synchronous.

> **Status:** new in 1.0.0, and released with AvroSharp at the same version. It follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.Kafka.html) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.Aws.Glue.Kafka --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Aws.Glue.Kafka depends on AvroSharp.Aws.Glue and Confluent.Kafka 2, and targets .NET 8 and later, and .NET Standard 2.0.

## Use

```csharp
var glue = new AmazonGlueClient();
var options = new AvroSharpGlueOptions { RegistryName = "shop", AutoRegisterSchemas = true };

using var producer = new ProducerBuilder<string, Order>(producerConfig)
    .SetAvroSharpGlueValueSerializer(glue, options)
    .Build();

using var consumer = new ConsumerBuilder<string, Order>(consumerConfig)
    .SetAvroSharpGlueValueDeserializer(glue, options)
    .Build();
```

The [guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html) covers the settings, moving from AWS's serializer, and the behavior to know.
