# Generated types

The `AvroSharp.Generators` source generator turns your `.avsc` schema files into C# types as the project builds, with serializers that call the binary encoder directly: no reflection, so they work with Native AOT and trimming. This page sets up a project, uses the types, and points to where each option is described.

## The project

Reference the package, and pass the schema files to the compiler as `AdditionalFiles`:

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" Version="..." />
  <AdditionalFiles Include="Schemas\*.avsc" />
</ItemGroup>
```

The package brings in the `AvroSharp` runtime package. The generator runs inside the compiler, so it needs the .NET 10 SDK or Visual Studio 2026. The generated code runs on .NET 8 and later, .NET Standard 2.0 and 2.1, and .NET Framework, from C# 7.2 on.

Every `.avsc` file is generated, and all of them are read together, so one file can use a type another defines. Here `order.avsc` has a field of type `com.example.crm.Customer`, which `customer.avsc` defines:

```json
{"type":"record","name":"Customer","namespace":"com.example.crm","fields":[
  {"name":"customer_id","type":"long"},
  {"name":"display_name","type":"string"}
]}
```

## Using the types

Each record becomes a `partial class` with a property per field. Field names become PascalCase properties, and logical types become .NET types: `uuid` is a `Guid`, `timestamp-millis` a `DateTimeOffset` and `decimal` a `decimal`:

```csharp
var order = new Order
{
    OrderId = Guid.NewGuid(),
    Placed = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
    Total = 129.95m,
    Customer = new Customer { CustomerId = 42, DisplayName = "Ada" },
};
```

`ToAvroBytes` and `FromAvroBytes` write and read one value in Avro's binary encoding:

```csharp
byte[] bytes = order.ToAvroBytes();
Order copy = Order.FromAvroBytes(bytes);
```

A new object starts with its schema's defaults, as reading data that lacks a field does. Each generated type also has its schema, as the static `Schema` property, and on .NET 8 and later implements [`IAvroSerializable<TSelf>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html), which [`AvroSerializer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.html) and the generic container and message APIs (`AvroFileWriter.Create<T>(stream)`) use.

## Namespaces

An Avro namespace becomes the C# namespace unless the project maps it. `AvroSharpNamespace` names the C# namespace of types that have no Avro namespace, and `AvroSharpNamespaceMap` maps Avro namespaces to C# ones:

```xml
<PropertyGroup>
  <AvroSharpNamespace>Shop</AvroSharpNamespace>
  <AvroSharpNamespaceMap>
    com.example.shop:Shop.Orders;
    com.example.crm:Shop.Customers
  </AvroSharpNamespaceMap>
</PropertyGroup>
```

A schema with no namespace, such as `heartbeat.avsc`, then lands in `Shop`:

```csharp
var heartbeat = new Heartbeat { ServiceName = "checkout", SentAt = order.Placed };
Heartbeat heartbeatCopy = Heartbeat.FromAvroBytes(heartbeat.ToAvroBytes());
```

Only the C# names change. The schemas keep their Avro names, so the data and the fingerprints stay the same.

## Reading older data

`FromAvroBytes(bytes, writerSchema)` reads data written with another version of the schema, by the same rules as the generic reader: see [Schema evolution](schema-evolution.md). Container files carry their writer schema, and the [GeneratedTypes sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratedTypes) reads a file an older version wrote.

## Next

- The [GeneratorPackage sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratorPackage) is this page's project, with every MSBuild setting and how to see the generated files.
- [Code generation](../code-generation.md) has every option, the full type mapping, the diagnostics, and the `avrosharp` tool for code that is checked in.
- [Logical types](logical-types.md) and [Container files](containers.md).
