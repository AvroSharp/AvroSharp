# AvroSharp.Generators

A C# source generator that turns Avro schema files (`.avsc`) into C# types with serializers for [AvroSharp](https://github.com/zcsizmadia/AvroSharp). The generated code uses no reflection, so it works with Native AOT and trimming.

> **Status:** early development, not yet released.

## Usage

Reference the package and pass your schema files to the compiler as `AdditionalFiles`:

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" Version="..." />
  <AdditionalFiles Include="Schemas\*.avsc" />
</ItemGroup>
```

The package brings in the `AvroSharp` runtime package. Each named type becomes a C# type: a record becomes a `partial class`, an enum a C# enum, and a fixed type a class that wraps exactly its number of bytes. Records get serializers that call `AvroWriter`/`AvroReader` directly:

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);
var upgraded = shop.Order.FromAvroBytes(oldBytes, writerSchema);   // schema evolution
```

## Options (MSBuild properties)

| Property | Values | Effect |
|---|---|---|
| `AvroSharpNamespace` | a C# namespace | The namespace of types without an Avro namespace |
| `AvroSharpPropertyNames` | `avro` | Keeps the Avro field names, as Apache's `avrogen` does, instead of PascalCase |
| `AvroSharpLogicalTypes` | `raw` | Keeps logical types as their underlying types instead of `DateOnly`, `Guid`, `decimal`, … |
| `AvroSharpApacheCompatible` | `true` | Generated types also implement Apache.Avro's `ISpecificRecord` (requires a reference to Apache.Avro) |

## Requirements

The generator needs the .NET 10 SDK or Visual Studio 2026, because it runs AvroSharp inside the compiler (see [#20](https://github.com/zcsizmadia/AvroSharp/issues/20)). The generated code works on every target AvroSharp supports: .NET 8, 9 and 10, .NET Standard 2.0 and 2.1, and .NET Framework.

See the [repository README](https://github.com/zcsizmadia/AvroSharp#code-generation-from-schema-files) for unions, logical types and the Apache.Avro compatibility mode.
