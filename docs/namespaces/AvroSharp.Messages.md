---
uid: AvroSharp.Messages
summary: *content
---
Single messages rather than files: the single-object encoding of the specification, and schema-registry framing.

- **Single-object encoding:** [`AvroMessage`](xref:AvroSharp.Messages.AvroMessage) writes messages and [`AvroMessageReader`](xref:AvroSharp.Messages.AvroMessageReader) creates readers. A reader finds the writer schema by its fingerprint through an [`IAvroSchemaResolver`](xref:AvroSharp.Messages.IAvroSchemaResolver); [`AvroSchemaStore`](xref:AvroSharp.Messages.AvroSchemaStore) is the in-memory one.
- **Schema registries:** [`AvroRegistryMessage`](xref:AvroSharp.Messages.AvroRegistryMessage) and [`AvroRegistryMessageReader`](xref:AvroSharp.Messages.AvroRegistryMessageReader), with an [`AvroRegistryFraming`](xref:AvroSharp.Messages.AvroRegistryFraming) for the registry's layout (Confluent, Apicurio, AWS Glue). A reader finds the schema by its [`AvroSchemaId`](xref:AvroSharp.Messages.AvroSchemaId) through an [`IAvroSchemaIdResolver`](xref:AvroSharp.Messages.IAvroSchemaIdResolver); [`AvroSchemaIdStore`](xref:AvroSharp.Messages.AvroSchemaIdStore) is the in-memory one. [`ConfluentSchemaIdHeader`](xref:AvroSharp.Messages.ConfluentSchemaIdHeader) handles a Confluent schema ID in a Kafka message header.

Messages hold generated types (<xref:AvroSharp.Serialization>) or the generic values of <xref:AvroSharp.Generic>.
