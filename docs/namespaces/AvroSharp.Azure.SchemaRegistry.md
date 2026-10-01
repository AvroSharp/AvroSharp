---
uid: AvroSharp.Azure.SchemaRegistry
summary: *content
---
The `AvroSharp.Azure.SchemaRegistry` package: an Azure Schema Registry serializer for Event Hubs and Service Bus messages, without Apache.Avro.
- [`AvroSharpSchemaRegistrySerializer`](xref:AvroSharp.Azure.SchemaRegistry.AvroSharpSchemaRegistrySerializer) writes values into a `MessageContent`, or a type derived from it such as `EventData` or `ServiceBusMessage`, and reads them back.
- The message's content type is `avro/binary+<schema ID>`, as with Microsoft's `SchemaRegistryAvroSerializer`.
- [`AvroSharpSchemaRegistrySerializerOptions`](xref:AvroSharp.Azure.SchemaRegistry.AvroSharpSchemaRegistrySerializerOptions) chooses whether schemas are registered.

The [Azure Schema Registry guide](https://avrosharp.github.io/AvroSharp/docs/azure-schema-registry.html) shows how to move from Microsoft's serializer.
