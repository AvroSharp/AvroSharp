---
uid: AvroSharp.Aws.Glue.Kafka
summary: *content
---
The `AvroSharp.Aws.Glue.Kafka` package: Confluent.Kafka serializers and deserializers for AWS Glue Schema Registry, on [`AvroSharpGlueSerializer`](xref:AvroSharp.Aws.Glue.AvroSharpGlueSerializer).
- [`AvroSharpGlueKafkaSerializer<T>`](xref:AvroSharp.Aws.Glue.Kafka.AvroSharpGlueKafkaSerializer`1) and [`AvroSharpGlueKafkaDeserializer<T>`](xref:AvroSharp.Aws.Glue.Kafka.AvroSharpGlueKafkaDeserializer`1) stand in for AWS's `GlueSchemaRegistryKafkaSerializer` and `GlueSchemaRegistryKafkaDeserializer`. Each is both asynchronous and synchronous.
- [`AvroSharpGlueKafkaExtensions`](xref:AvroSharp.Aws.Glue.Kafka.AvroSharpGlueKafkaExtensions) sets them on Confluent.Kafka's producer and consumer builders.

The [AWS Glue guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html) covers the settings and moving from AWS's serializer.
