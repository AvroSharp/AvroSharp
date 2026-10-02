---
uid: AvroSharp.Aws.Glue
summary: *content
---
The `AvroSharp.Aws.Glue` package: a managed AWS Glue Schema Registry serializer, for every platform, without Apache.Avro or a native library.
- [`AvroSharpGlueSerializer`](xref:AvroSharp.Aws.Glue.AvroSharpGlueSerializer) writes and reads messages in AWS's wire format: `0x03`, the compression byte, the schema version UUID, then the Avro data, zlib-compressed or not.
- [`AvroSharpGlueKafkaSerializer<T>`](xref:AvroSharp.Aws.Glue.AvroSharpGlueKafkaSerializer`1) and [`AvroSharpGlueKafkaDeserializer<T>`](xref:AvroSharp.Aws.Glue.AvroSharpGlueKafkaDeserializer`1) are Confluent.Kafka serializers on it, set with [`AvroSharpGlueKafkaExtensions`](xref:AvroSharp.Aws.Glue.AvroSharpGlueKafkaExtensions).
- [`AvroSharpGlueOptions`](xref:AvroSharp.Aws.Glue.AvroSharpGlueOptions) holds the settings, with the defaults of AWS's serializer.

The [AWS Glue guide](https://avrosharp.github.io/AvroSharp/docs/aws-glue.html) shows how to move from AWS's serializer.
