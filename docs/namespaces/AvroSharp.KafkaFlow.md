---
uid: AvroSharp.KafkaFlow
summary: *content
---
The `AvroSharp.KafkaFlow` package: KafkaFlow serializer middleware for Confluent Schema Registry on [AvroSharp.Confluent](xref:AvroSharp.Confluent), without Apache.Avro.
- [`AvroSharpKafkaFlowExtensions`](xref:AvroSharp.KafkaFlow.AvroSharpKafkaFlowExtensions) adds it to producers and consumers, in place of KafkaFlow's `AddSchemaRegistryAvroSerializer` and `AddSchemaRegistryAvroDeserializer`.
- [`AvroSharpKafkaFlowSerializer`](xref:AvroSharp.KafkaFlow.AvroSharpKafkaFlowSerializer) and [`AvroSharpKafkaFlowDeserializer`](xref:AvroSharp.KafkaFlow.AvroSharpKafkaFlowDeserializer) are KafkaFlow's `ISerializer` and `IDeserializer`.
- [`AvroSharpMessageTypeResolver`](xref:AvroSharp.KafkaFlow.AvroSharpMessageTypeResolver) picks each consumed message's type from its writer's schema, for topics with several record types.

The [KafkaFlow guide](https://avrosharp.github.io/AvroSharp/docs/kafkaflow.html) shows how to move from KafkaFlow's Confluent Avro serializer.
