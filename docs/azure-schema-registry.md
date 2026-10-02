# Azure Schema Registry

[AvroSharp.Azure.SchemaRegistry](https://www.nuget.org/packages/AvroSharp.Azure.SchemaRegistry) writes and reads Avro messages with [Azure Schema Registry](https://learn.microsoft.com/azure/event-hubs/schema-registry-overview), for Event Hubs and Service Bus. It replaces Microsoft's `Microsoft.Azure.Data.SchemaRegistry.ApacheAvro`, without Apache.Avro.
- **The same message format:** the body is the value's Avro encoding, and the content type is `avro/binary+<schema ID>`. So producers and consumers can move one at a time.
- **The same API:** `Serialize` and `Deserialize`, to and from `MessageContent` and the types derived from it: Event Hubs' `EventData` and Service Bus' `ServiceBusMessage`.
- **Types:** generated from `.avsc` files, or your own types marked `[AvroSerializable]`, and generic records.
- **Schema evolution:** each message is read in its writer's schema and resolved to your type's.

```
dotnet add package AvroSharp.Azure.SchemaRegistry --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Azure.SchemaRegistry depends on Azure.Data.SchemaRegistry 1.2.0 or later (below 2.0), and is released with AvroSharp, at the same version. It targets .NET 8 and later, and .NET Standard 2.0.

On this page:
- [Use](#use)
- [Moving from Microsoft's Avro serializer](#moving-from-microsofts-avro-serializer)
- [Behavior to know](#behavior-to-know)

## Use

```csharp
using AvroSharp.Azure.SchemaRegistry;
using Azure.Data.SchemaRegistry;
using Azure.Identity;
using Azure.Messaging.EventHubs;

var registry = new SchemaRegistryClient("<namespace>.servicebus.windows.net", new DefaultAzureCredential());
var serializer = new AvroSharpSchemaRegistrySerializer(registry, "orders");   // the schema group

// Order is generated from Order.avsc, or is a partial class marked [AvroSerializable].
EventData message = await serializer.SerializeAsync<EventData, Order>(order);
await producer.SendAsync([message]);

// In the consumer:
Order received = await serializer.DeserializeAsync<Order>(eventData);
```

- **Writing:** the serializer registers the value's schema in the group under the schema's full name, or looks up its ID, the first time. Then it writes the body and sets the content type. Registering needs `AutoRegisterSchemas = true` in [`AvroSharpSchemaRegistrySerializerOptions`](xref:AvroSharp.Azure.SchemaRegistry.AvroSharpSchemaRegistrySerializerOptions). Without it, as in Microsoft's serializer, the schema must already be registered.
- **Reading:** the serializer fetches the writer's schema by the ID in the content type, once per ID, and resolves the data to `Order`'s schema. A reader needs no group: `new AvroSharpSchemaRegistrySerializer(registry)`.
- **Generic records:** write a `GenericRecord`, or an `AvroValue` holding one, with its own schema. Read one with `DeserializeAsync<GenericRecord>(message)`, in the writer's schema.
- **Overloads:** each method has a synchronous form. There are also overloads that take the value's `Type`, and a message type, at run time.

## Moving from Microsoft's Avro serializer

1. **Generate the types with AvroSharp.Generators.**
   - **Moving gradually?** Set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, and the generated classes also implement Apache's `ISpecificRecord`. Both serializers then work with the same classes, as this repository's tests use them. See [the Apache.Avro compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode).
   - **Moving at once?** Use AvroSharp's own types, without Apache.Avro.
2. **Replace the serializer:**
   - `SchemaRegistryAvroSerializer` → `AvroSharpSchemaRegistrySerializer`, with the same constructor arguments;
   - `SchemaRegistryAvroSerializerOptions` → `AvroSharpSchemaRegistrySerializerOptions`.

   The method calls stay the same.
3. **The messages don't change:**
   - the same body, which the tests compare with Microsoft's serializer byte for byte;
   - the same content type format;
   - each reads what the other writes.

## Behavior to know

- **The schema text.** AvroSharp registers a schema as Java's Avro writes it (`Schema.toString()`), which is likely what Azure's Java serializer registers too (not checked here). Microsoft's .NET serializer registers Apache.Avro .NET's text of the same schema, with the keys in another order, and with each nested named type's namespace written out even when it's the enclosing one's. With auto-registration this doesn't matter: each registration gives an ID that readers fetch. Without it, a schema registered only from Microsoft's .NET serializer might not be found by its text. Whether the service compares schemas by text or by meaning isn't documented, and isn't verified here. Register the schema from AvroSharp, or with auto-registration, if that happens.
- **Named schemas only.** The registry names each schema, so a value of a primitive schema, such as a `string`, can't be written.
- **Caching:** the IDs of the schemas written and the writer schemas read are kept for the serializer's lifetime. Microsoft's serializer keeps its last 128.
