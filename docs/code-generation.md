# Code generation: the AvroSharp.Generators source generator

[AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) turns Avro schema files (`.avsc`) into C# types with serializers, while your project builds. The generated code has no reflection or runtime schema lookups, and works with Native AOT and trimming. The [`avrosharp` command-line tool](cli.md) produces the same code outside the build.

On this page:
- [Set up a project](#set-up-a-project)
- [What is generated](#what-is-generated)
- [Using the generated types](#using-the-generated-types)
- [MSBuild properties](#msbuild-properties)
- [Type mapping](#type-mapping)
- [Schema evolution](#schema-evolution)
- [Migrating from avrogen: the Apache.Avro compatibility mode](#migrating-from-avrogen-the-apacheavro-compatibility-mode)
- [Diagnostics](#diagnostics)
- [Requirements](#requirements)
- [Using the generator from source](#using-the-generator-from-source)

## Set up a project

Reference the package, and pass your schema files to the compiler as `AdditionalFiles`:

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" Version="..." />
  <AdditionalFiles Include="Schemas\**\*.avsc" />
</ItemGroup>
```

That is all. The package brings in the [AvroSharp](https://www.nuget.org/packages/AvroSharp) runtime package. The types appear as you edit the schemas; no build step or checked-in code is needed. See [the GeneratorPackage sample](https://github.com/zcsizmadia/AvroSharp/tree/main/samples/GeneratorPackage) for a complete project with every setting, and [GeneratedTypes](https://github.com/zcsizmadia/AvroSharp/tree/main/samples/GeneratedTypes) for container files and schema evolution.

Schema files may refer to named types that other files define, in any order. A type defined identically in several files, as schema sets written for Apache's tooling often are, is generated once.

## What is generated

Each named type becomes one C# type:

| Avro | C# |
|---|---|
| `record` | a `partial class`, with a property per field and serializers |
| `enum` | a C# `enum` |
| `fixed` | a class that wraps exactly its number of bytes |

The C# namespace is the Avro namespace. Types without one go into the namespace in `AvroSharpNamespace`, or the global namespace.

Each record gets:
- `Write(ref AvroWriter, T)` and `Read(ref AvroReader)`: serializers that call `AvroWriter`/`AvroReader` directly, in schema order;
- `ToAvroBytes()`, `FromAvroBytes(...)`, `TryWriteAvroBytes(Span<byte>, out int)` and `WriteAvroBytes(IBufferWriter<byte>)`;
- `ReadFrom(ref AvroReader)`, which fills an existing instance and reuses its lists, dictionaries and records;
- `FromAvroBytes(bytes, writerSchema)`, which reads data written with another version of the schema;
- a static `Schema`, and the interfaces `IAvroSpecificRecord`, `IAvroWritable` and `IAvroReadable`. On .NET 8 and later, with C# 11 or later, also `IAvroSerializable<T>`.

`new Order()` gives every field that has a schema default its default, as reading data that lacks the field would.

## Using the generated types

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);

// Into your own buffer writer.
var writer = new AvroWriter(bufferWriter);
shop.Order.Write(ref writer, order);
writer.Flush();

// Without allocating: into caller memory, or into a reused buffer writer.
Span<byte> buffer = stackalloc byte[512];
if (order.TryWriteAvroBytes(buffer, out var written)) { /* buffer[..written] */ }

// One instance for a stream of values: its collections are filled again.
var reader = new AvroReader(data);
order.ReadFrom(ref reader);
```

On .NET 8 and later, the APIs that take a type need no delegates, through `IAvroSerializable<T>`:

```csharp
byte[] same = AvroSerializer.Serialize(order);
using var file = AvroFileWriter.Create<shop.Order>(stream);
using var input = AvroFileReader.Open<shop.Order>(stream);           // resolves other schema versions
byte[] message = AvroMessage.ToArray(order);                          // single-object encoding
```

On every target, pass the serializers instead: `AvroFileWriter.Create<Order>(stream, Order.Schema, Order.Write)` and `AvroFileReader.Open<Order>(stream, _ => Order.Read)`. The [guide](../README.md) shows [container files](../README.md#object-container-files), [single-object messages](../README.md#single-object-encoding), [schema registries](../README.md#schema-registries) and [streams](../README.md#streams-of-objects) with generated types.

## MSBuild properties

Set these in the project file (or a `Directory.Build.props`). Each one has the same values as the [`avrosharp gen`](cli.md) option of the same name and the `CodeGenOptions` property of the same name. Values are case-insensitive, and a value the generator doesn't recognize is a warning (AVROGEN006), not ignored silently. The C# version, nullable annotations and `DateOnly` come from the project.

| Property | Values | Effect |
|---|---|---|
| `AvroSharpNamespace` | a C# namespace | The namespace of types that have no Avro namespace (`--namespace`). |
| `AvroSharpNamespaceMap` | `avro.ns:CSharp.Ns`, separated by `;` | Puts types of an Avro namespace, or of a namespace under it, into another C# namespace, as avrogen's `--namespace` does (`--namespace-map`). The longest matching entry wins. Not with `AvroSharpApacheCompatible`. |
| `AvroSharpPropertyNames` | `pascal`, `avro` | `pascal` (the default) converts field names to PascalCase: `customer_name` becomes `CustomerName`, `USER_ID` becomes `UserId`. `avro` keeps the field names as written, as Apache's `avrogen` does, escaping C# keywords (`@class`). |
| `AvroSharpLogicalTypes` | `native`, `raw` | `native` (the default) maps logical types to .NET types. `raw` keeps logical types as their underlying types (`int`, `long`, `string`, `byte[]`) instead of `DateOnly`, `Guid`, `decimal` and the others. |
| `AvroSharpApacheCompatible` | `true`, `false` | `true` turns on the [Apache.Avro compatibility mode](#migrating-from-avrogen-the-apacheavro-compatibility-mode). |

```xml
<PropertyGroup>
  <AvroSharpNamespace>Acme.Events</AvroSharpNamespace>
  <AvroSharpNamespaceMap>com.acme.events:Acme.Events;com.acme.common:Acme.Common</AvroSharpNamespaceMap>
  <AvroSharpPropertyNames>avro</AvroSharpPropertyNames>
</PropertyGroup>
```

The C# version and the target framework come from the project: with C# 7.3 (netstandard2.0 and .NET Framework projects) the code has no nullable annotations, and on targets without `DateOnly`/`TimeOnly` it uses `DateTime` and `TimeSpan`.

## Type mapping

| Avro | C# |
|---|---|
| `null` | `object?`, always `null` |
| `boolean`, `int`, `long`, `float`, `double` | `bool`, `int`, `long`, `float`, `double` |
| `bytes`, `string` | `byte[]`, `string` |
| `array`, `map` | `List<T>`, `Dictionary<string, T>` |
| a union of `null` and one type | that type, nullable |
| other unions | `object?` |
| `date` | `DateOnly` (`DateTime` without `DateOnly`) |
| `time-millis`, `time-micros` | `TimeOnly` (`TimeSpan` without `TimeOnly`) |
| `timestamp-millis`, `timestamp-micros` | `DateTimeOffset` |
| `local-timestamp-millis`, `local-timestamp-micros` | `DateTime` |
| `uuid` (on `string` or `fixed(16)`) | `Guid` |
| `decimal` with a precision up to 28 | `decimal`, written exactly or rejected, never rounded |
| other logical types (`duration`, `timestamp-nanos`, wider decimals) | their underlying type, with its meaning in the property's documentation |

The generator reports a union whose branches map to the same C# type (for example a `uuid` string and a `uuid` fixed, both `Guid`) as an error, and suggests `AvroSharpLogicalTypes=raw`.

## Schema evolution

A generated type reads data written with any compatible version of its schema, following the specification's resolution rules: added fields take their defaults, removed fields are skipped, fields match by name or alias, and numbers are promoted.

```csharp
var upgraded = shop.Order.FromAvroBytes(oldBytes, writerSchema);
```

The plan for each writer schema is built once and cached per type. Container files and single-object messages record the writer schema, so their readers resolve it without code: `AvroFileReader.Open<Order>(stream)` reads a file an older version wrote.

## Migrating from avrogen: the Apache.Avro compatibility mode

With `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, in a project that references Apache.Avro, the generated types work with both libraries, so code can move one call site at a time. [AvroSharp and Apache.Avro](apache-avro.md#moving-from-apacheavro) describes the migration.

- Code written for `avrogen` classes compiles unchanged. Property names are avrogen's (the Avro field names) unless `AvroSharpPropertyNames` says otherwise, and types have avrogen's static `_SCHEMA` and instance `Schema` (Apache's `Avro.Schema`). AvroSharp's schema is `AvroSharpSchema`.
- Records also implement `Avro.Specific.ISpecificRecord`, and fixed types derive from `Avro.Specific.SpecificFixed`, so Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>` accept them.
- Logical types use Apache's .NET types: `DateTime`, `TimeSpan`, `Guid`, `Avro.AvroDecimal`.

Apache.Avro 1.12.2 has limits in this mode, which the tests pin: its specific writer cannot write a `decimal` on `fixed`, it rejects `uuid` on `fixed`, and it reads `local-timestamp` values as UTC instants in local time.

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| AVROGEN001 | Error | A schema file is not a valid Avro schema. The location is the file, line and column. |
| AVROGEN002 | Error | The project does not reference the AvroSharp runtime package. |
| AVROGEN003 | Error | Code generation failed, for example for a union of two branches with the same C# type. |
| AVROGEN004 | Error | `AvroSharpApacheCompatible` is set, but the project does not reference Apache.Avro. |
| AVROGEN005 | Info | A property was renamed to avoid a clash with another member (for example `user_id` and `userId` in one record). |
| AVROGEN006 | Warning | An `AvroSharp…` MSBuild property has a value the generator doesn't recognize, for example `AvroSharpLogicalTypes` set to `rwa`. The message names the property and the value used instead. |

## Requirements

- **To build:** the generator needs the .NET 10 SDK, or Visual Studio 2026 or later, because it runs AvroSharp inside the compiler ([#20](https://github.com/zcsizmadia/AvroSharp/issues/20)). Older SDKs cannot load it; [the design notes](design.md) record what fails.
- **To run:** the generated code works on every target AvroSharp supports: .NET 8, 9 and 10, .NET Standard 2.0 and 2.1, and so .NET Framework.

A build with an older SDK can use the [command-line tool](cli.md) instead, and compile the generated files as ordinary sources.

## Using the generator from source

A package reference is the supported way. A project in the same repository as a build of the generator can reference its project instead, as this repository's samples, tests and benchmarks do ([`build/UseLocalGenerator.targets`](https://github.com/zcsizmadia/AvroSharp/blob/main/build/UseLocalGenerator.targets)): as an `Analyzer` project reference, with the generator's own dependencies (`AvroSharp.dll` and `AvroSharp.CodeGen.dll`) added as analyzers, and the package's `build/AvroSharp.Generators.props` and `.targets` imported, which pass the `AvroSharp*` properties to the compiler. The package does all of this itself. [The GeneratorPackage sample](https://github.com/zcsizmadia/AvroSharp/tree/main/samples/GeneratorPackage#building-the-generator-from-source) has the details and caveats.
