![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/logo.png)

# AvroSharp.Azure.SchemaRegistry

An [Azure Schema Registry](https://learn.microsoft.com/azure/event-hubs/schema-registry-overview) serializer on [AvroSharp](https://github.com/AvroSharp/AvroSharp) for Event Hubs and Service Bus messages (`MessageContent`). It's a replacement for Microsoft's `Microsoft.Azure.Data.SchemaRegistry.ApacheAvro`, without Apache.Avro:
- **The same message format:** the Avro body, and the content type `avro/binary+<schema ID>`. The tests compare the body with Microsoft's serializer byte for byte, and each reads what the other writes.
- **The same API:** `Serialize` and `Deserialize`, to and from `MessageContent`, `EventData` and `ServiceBusMessage`, with sync and async forms.
- **Types:** generated from `.avsc` files, or your own C# types marked `[AvroSerializable]`, and generic records.
- **Schema evolution:** each message is read in its writer's schema and resolved to your type's.

> **Status:** new in the 1.0.0 release candidates, and released with AvroSharp at the same version. Its API may still change until 1.0.0; from then it follows [semantic versioning](https://semver.org/) with the rest of AvroSharp.

**[Guide](https://avrosharp.github.io/AvroSharp/docs/azure-schema-registry.html)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Azure.SchemaRegistry.html) · [AvroSharp documentation](https://avrosharp.github.io/AvroSharp/)

## Install

```
dotnet add package AvroSharp.Azure.SchemaRegistry --prerelease
dotnet add package AvroSharp.Generators --prerelease
```

AvroSharp.Azure.SchemaRegistry depends on Azure.Data.SchemaRegistry 1.2.0 or later (below 2.0), and targets .NET 8 and later, and .NET Standard 2.0.

## Use

```csharp
var registry = new SchemaRegistryClient("<namespace>.servicebus.windows.net", new DefaultAzureCredential());
var serializer = new AvroSharpSchemaRegistrySerializer(registry, "orders");   // the schema group

EventData message = await serializer.SerializeAsync<EventData, Order>(order);
Order received = await serializer.DeserializeAsync<Order>(message);
```

The schema must already be registered in the group, or set `AutoRegisterSchemas = true` in `AvroSharpSchemaRegistrySerializerOptions`. The [guide](https://avrosharp.github.io/AvroSharp/docs/azure-schema-registry.html) covers generic records, moving from Microsoft's serializer, and the behavior to know.
