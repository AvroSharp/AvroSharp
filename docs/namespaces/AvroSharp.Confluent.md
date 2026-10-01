---
uid: AvroSharp.Confluent
summary: *content
---
The `AvroSharp.Confluent` package: Confluent Schema Registry serializers and deserializers for Confluent.Kafka, without Apache.Avro.
- [`AvroSharpSerializer<T>`](xref:AvroSharp.Confluent.AvroSharpSerializer`1) and [`AvroSharpDeserializer<T>`](xref:AvroSharp.Confluent.AvroSharpDeserializer`1) handle generated types, `[AvroSerializable]` types and the primitives.
- [`AvroSharpGeneric`](xref:AvroSharp.Confluent.AvroSharpGeneric) handles generic values.
- [`AvroSharpSerdeExtensions`](xref:AvroSharp.Confluent.AvroSharpSerdeExtensions) sets them on Confluent.Kafka's producer and consumer builders.

They derive from Confluent's serializer base classes, so subject name strategies, registration, schema ID strategies, references and rules are Confluent's own code. [`AvroSharpSerializerConfig`](xref:AvroSharp.Confluent.AvroSharpSerializerConfig) and [`AvroSharpDeserializerConfig`](xref:AvroSharp.Confluent.AvroSharpDeserializerConfig) take the keys of Confluent's Avro serializer configuration. The package's [README](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Confluent) shows how to move from Confluent's Avro serializer and from Chr.Avro.
