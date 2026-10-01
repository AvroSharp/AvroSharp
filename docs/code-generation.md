# Code generation: the AvroSharp.Generators source generator

[AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) turns Avro schema files (`.avsc`) into C# types with serializers, while your project builds. The generated code has no reflection or runtime schema lookups, and works with Native AOT and trimming. The [`avrosharp` command-line tool](cli.md) produces the same code outside the build. The generator also writes schemas and serializers for your own C# types marked `[AvroSerializable]` ([below](#from-c-types-avroserializable)); the tool doesn't.

On this page:
- [Set up a project](#set-up-a-project)
- [What is generated](#what-is-generated)
- [Using the generated types](#using-the-generated-types)
- [MSBuild properties](#msbuild-properties)
- [Type mapping](#type-mapping)
- [Schema evolution](#schema-evolution)
- [Migrating from avrogen: the Apache.Avro compatibility mode](#migrating-from-avrogen-the-apacheavro-compatibility-mode)
- [From C# types: `[AvroSerializable]`](#from-c-types-avroserializable)
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

That is all. The package brings in the [AvroSharp](https://www.nuget.org/packages/AvroSharp) runtime package. The types appear as you edit the schemas; no build step or checked-in code is needed. See [the GeneratorPackage sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratorPackage) for a complete project with every setting, and [GeneratedTypes](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratedTypes) for container files and schema evolution.

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
- `Write(ref AvroWriter, T)` and `Read(ref AvroReader)`: serializers that call [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html)/[`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) directly, in schema order;
- `ToAvroBytes()`, `FromAvroBytes(...)`, `TryWriteAvroBytes(Span<byte>, out int)` and `WriteAvroBytes(IBufferWriter<byte>)`;
- `ReadFrom(ref AvroReader)`, which fills an existing instance and reuses its lists, dictionaries and records;
- `FromAvroBytes(bytes, writerSchema)`, which reads data written with another version of the schema;
- a static `Schema`, and the interfaces [`IAvroSpecificRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSpecificRecord.html), [`IAvroWritable`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroWritable.html) and [`IAvroReadable`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroReadable.html). On .NET 8 and later, with C# 11 or later, also [`IAvroSerializable<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html).

`new Order()` gives every field that has a schema default its default, as reading data that lacks the field would. A union field's default is a value of the first branch it fits, which need not be the union's first branch, as Avro 1.12 says.

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

On .NET 8 and later, the APIs that take a type need no delegates, through [`IAvroSerializable<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html): [`AvroSerializer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.html), [`AvroFileWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.html), [`AvroFileReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.html) and [`AvroMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.html):

```csharp
byte[] same = AvroSerializer.Serialize(order);
using var file = AvroFileWriter.Create<shop.Order>(stream);
using var input = AvroFileReader.Open<shop.Order>(stream);           // resolves other schema versions
byte[] message = AvroMessage.ToArray(order);                          // single-object encoding
```

On every target, pass the serializers instead: [`AvroFileWriter.Create<Order>(stream, Order.Schema, Order.Write)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.Create.html) and [`AvroFileReader.Open<Order>(stream, _ => Order.Read)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.Open.html). The [guide](../README.md) shows [container files](../README.md#object-container-files), [single-object messages](../README.md#single-object-encoding), [schema registries](../README.md#schema-registries) and [streams](../README.md#streams-of-objects) with generated types.

## MSBuild properties

Set these in the project file (or a `Directory.Build.props`). Each one has the same values as the [`avrosharp gen`](cli.md) option of the same name and the [`CodeGenOptions`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.html) property of the same name. Values are case-insensitive, and a value the generator doesn't recognize is a warning (AVROGEN006), and the default is used, instead of being ignored silently. The C# version, nullable annotations and `DateOnly` come from the project.

| Property | Values | Effect |
|---|---|---|
| `AvroSharpNamespace` | a C# namespace | The namespace of types that have no Avro namespace (`--namespace`). |
| `AvroSharpNamespaceMap` | `avro.ns:CSharp.Ns` entries, separated by `;` or `,`, or written one per line | Puts types of an Avro namespace, or of a namespace under it, into another C# namespace, as avrogen's `--namespace` does (`--namespace-map`). The longest matching entry wins. An entry that is not two namespaces around a colon is a warning (AVROGEN006) and is ignored; an Avro namespace mapped twice keeps its first entry. Not with `AvroSharpApacheCompatible`. |
| `AvroSharpPropertyNames` | `pascal`, `avro` | `pascal` (the default) converts field names to PascalCase: `customer_name` becomes `CustomerName`, `USER_ID` becomes `UserId`. `avro` keeps the field names as written, as Apache's `avrogen` does, escaping C# keywords (`@class`). |
| `AvroSharpLogicalTypes` | `native`, `raw` | `native` (the default) maps logical types to .NET types. `raw` keeps logical types as their underlying types (`int`, `long`, `string`, `byte[]`) instead of `DateOnly`, `Guid`, `decimal` and the others. For one schema, add `"avrosharp.raw": true` to it instead (see below). |
| `AvroSharpApacheCompatible` | `true`, `false` | `true` turns on the [Apache.Avro compatibility mode](#migrating-from-avrogen-the-apacheavro-compatibility-mode). |

```xml
<PropertyGroup>
  <AvroSharpNamespace>Acme.Events</AvroSharpNamespace>
  <AvroSharpNamespaceMap>com.acme.events:Acme.Events;com.acme.common:Acme.Common</AvroSharpNamespaceMap>
  <AvroSharpPropertyNames>avro</AvroSharpPropertyNames>
</PropertyGroup>
```

The C# version and the target framework come from the project: with C# 7.3 (netstandard2.0 and .NET Framework projects) the code has no nullable annotations, and on targets without `DateOnly`/`TimeOnly` it uses `DateTime` and `TimeSpan`. The generated code needs C# 7.2 or later: with C# 7.0 or 7.1 the generator reports AVROGEN003 and generates nothing.

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

To keep one logical type's underlying type, add `"avrosharp.raw": true` to its schema, as in `{"type":"long","logicalType":"timestamp-millis","avrosharp.raw":true}`. That property becomes a `long`, and the other fields keep their .NET types. This is for values that .NET's types can't hold but Java reads, such as a `timestamp-millis` of `Long.MaxValue` used as a "never" sentinel. A generated type with a `DateTimeOffset` property fails on such a value, and the error says how to read it. Custom properties are not part of the canonical form, so the schema's fingerprint doesn't change. The setting is not available with `AvroSharpApacheCompatible`.

## Schema evolution

A generated type reads data written with any compatible version of its schema, following the specification's resolution rules: added fields take their defaults, removed fields are skipped, fields match by name or alias, and numbers are promoted.

```csharp
var upgraded = shop.Order.FromAvroBytes(oldBytes, writerSchema);
```

The plan for each writer schema is built once and cached per type. Container files and single-object messages record the writer schema, so their readers resolve it without code: [`AvroFileReader.Open<Order>(stream)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.Open.html) reads a file an older version wrote.

## Migrating from avrogen: the Apache.Avro compatibility mode

With `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`, in a project that references Apache.Avro, the generated types work with both libraries, so code can move one call site at a time. [AvroSharp and Apache.Avro](apache-avro.md#moving-from-apacheavro) describes the migration.

- Code written for `avrogen` classes compiles unchanged. Property names are avrogen's (the Avro field names) unless `AvroSharpPropertyNames` says otherwise, and types have avrogen's static `_SCHEMA` and instance `Schema` (Apache's `Avro.Schema`). AvroSharp's schema is `AvroSharpSchema`.
- Records also implement `Avro.Specific.ISpecificRecord`, and fixed types derive from `Avro.Specific.SpecificFixed`, so Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>` accept them.
- Logical types use Apache's .NET types: `DateTime`, `TimeSpan`, `Guid`, `Avro.AvroDecimal`.

Apache.Avro 1.12.2 has limits in this mode, which the tests pin: its specific writer cannot write a `decimal` on `fixed`, it rejects `uuid` on `fixed`, and it reads `local-timestamp` values as UTC instants in local time.

## From C# types: `[AvroSerializable]`

The same package also works the other way round: mark a `partial` class or record class with [`[AvroSerializable]`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializableAttribute.html), and the generator writes its schema and serializers from its members.

```csharp
[AvroSerializable(Namespace = "acme.orders")]
public partial class Order
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public string? Note { get; set; }                  // ["null","string"], default null
    public List<OrderLine> Lines { get; set; } = [];  // OrderLine is [AvroSerializable] too
    public Status Status { get; set; }                 // a C# enum
    public DateTimeOffset PlacedAt { get; set; }       // long, timestamp-micros

    [AvroDecimal(18, 2)] public decimal Total { get; set; }
    [AvroName("legacy_ref"), AvroAlias("ref")] public string? Reference { get; set; }
    [AvroIgnore] public decimal CachedTax { get; set; }
}
```

**What the type gets:** the members a type generated from a `.avsc` file has:
- `Schema` and `SchemaJson`;
- `ToAvroBytes`, `FromAvroBytes` (also from another version of the schema), `Write` and `Read`;
- `IAvroWritable`, `IAvroReadable`, and `IAvroSerializable<T>` on .NET 8 and later.

So [`AvroSerializer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.html), container files, single-object messages and the registry readers take it as they take generated types. The serializers are the same code as the `.avsc` path's, so they're as fast, with no reflection.

**The fields** are the type's public settable properties and public fields, in declaration order, inherited ones first. `[AvroIgnore]` leaves one out.

**Field names** are the member names as written, as Apache Avro's Java reflection and Apache.Avro's `[AvroField]` matching use them.
- `[AvroSerializable(FieldNames = AvroNaming.CamelCase)]` writes `OrderId` as `orderId`.
- `[assembly: AvroNamingPolicy(AvroNaming.CamelCase)]` does that for the whole assembly.
- If the schemas are read by Java or other languages, set camelCase for the assembly, since their field names are camelCase by convention.
- `[AvroName]` renames one field or enum symbol.

**Types:**

| C# | Avro |
|---|---|
| `bool`, `int`, `long`, `float`, `double`, `string`, `byte[]` | `boolean`, `int`, `long`, `float`, `double`, `string`, `bytes` |
| `T?`, or a reference type annotated `?` | `["null", T]`, with a default of `null` |
| a C# enum, with values 0, 1, 2 and so on | `enum`. `[AvroEnumDefault]` on a member sets the enum's default. |
| an `[AvroSerializable]` class | `record` |
| `List<T>`, `Dictionary<string, T>` | `array`, `map` |
| `Guid` | `string` with `uuid`, or `fixed(16)` with `uuid` under `[AvroFixed(16)]` |
| `decimal` with `[AvroDecimal(precision, scale)]` | `bytes` with `decimal`, or `fixed` with `[AvroFixed(size)]` |
| `DateOnly`, `TimeOnly`, `DateTimeOffset` | `date`, `time-micros`, `timestamp-micros`. `[AvroLogicalType("timestamp-millis")]` and others change it. |
| `DateTime` | Needs `[AvroLogicalType("local-timestamp-micros")]` (or `-millis`): a `DateTime`'s `Kind` leaves UTC and local time ambiguous, so a UTC timestamp is a `DateTimeOffset`. |
| `byte[]` with `[AvroFixed(size)]` | `fixed` |
| `object` with `[AvroUnion(typeof(A), typeof(B))]` | a union of those records (`null` first when the member is `object?`) |

**Other attributes:**
- `[AvroDefault("json")]`: a field's default, as Avro JSON, which readers of older data use.
- `[AvroAlias]`: names from earlier versions.
- `[AvroDoc]`: a `doc`. The XML `<summary>` is used when the project builds documentation.
- `[AvroField(Order = n)]`: a field's position, needed only when the fields are declared in more than one file of a partial type.

**Not yet supported** (each is an error that says so): `init`-only members, primary constructors, types nested in other types, generic types, and narrow integer types such as `short`. [The design](design.md#65-the-attribute-driven-generator-31) lists what comes later.

### Finding a type's serializers: `AvroTypes`

[`AvroTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroTypes.html) gives a type's schema and its read and write functions, by type argument (`AvroTypes.Get<Order>()`) or by `Type` (`AvroTypes.TryGet(type, out var info)`), without reflection. It's for integrations and generic code, and for code that has only a `Type`.

**What's registered:**
- Every generated type, from `.avsc` files or `[AvroSerializable]`. On .NET 5 and later with C# 9 or later, a type registers itself when its assembly loads. Elsewhere, call `AvroTypes.Register(Order.AvroTypeInfo)` once.
- The primitives: `bool`, `int`, `long`, `float`, `double`, `string` and `byte[]`.

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| AVROGEN001 | Error | A schema file is not a valid Avro schema. The location is the file, line and column. |
| AVROGEN002 | Error | The project does not reference the AvroSharp runtime package. |
| AVROGEN003 | Error | Code generation failed, for example for a union of two branches with the same C# type, or because the project uses C# 7.0 or 7.1. |
| AVROGEN004 | Error | `AvroSharpApacheCompatible` is set, but the project does not reference Apache.Avro. |
| AVROGEN005 | Info | A property or type was renamed to avoid a clash with another member or a C# rule (for example `user_id` and `userId` in one record). |
| AVROGEN006 | Warning | An `AvroSharp…` MSBuild property has a value the generator doesn't recognize, for example `AvroSharpLogicalTypes` set to `rwa`. The message names the property and the value used instead. Also reported for an `AvroSharpNamespaceMap` entry that is not valid or maps a namespace a second time. |

The `[AvroSerializable]` generator reports these at the code. On an error, the type gets no generated code:

| ID | Severity | Meaning |
|---|---|---|
| AVROGEN101 | Error | The type is not a `partial`, non-abstract class or record class. |
| AVROGEN102 | Error | A member's type has no Avro mapping, or is not the type its field is read as. The message names the type to use. |
| AVROGEN103 | Error | A `decimal` member has no `[AvroDecimal]`. |
| AVROGEN104 | Error | A name is not a valid Avro name. |
| AVROGEN105 | Error | Two members have the same Avro field name. |
| AVROGEN106 | Error | An `[AvroDefault]` is not JSON, or not a value of the field's schema. |
| AVROGEN107 | Error | The type is generic, or nested in another type. |
| AVROGEN108 | Error | The type has a primary constructor. |
| AVROGEN109 | Error | The fields are declared in more than one file without `[AvroField(Order = n)]` on each. |
| AVROGEN110 | Error | `[AvroUnion]` is not on an `object` member, or lists a type that is not a class. |
| AVROGEN111 | Error | A member uses a class that is not `[AvroSerializable]`, or whose attribute has errors. |
| AVROGEN112 | Warning | An Avro attribute doesn't apply to the member it's on, and is ignored. |
| AVROGEN113 | Error | Two C# types define the same Avro name. |
| AVROGEN114 | Error | A `DateTime` member has no logical type. |
| AVROGEN115 | Error | A member is `init`-only. |
| AVROGEN116 | Error | An enum's values are not 0, 1, 2 and so on, in declaration order. |
| AVROGEN117 | Error | A member has the name of one the generator adds, such as `Schema` or `Write`. |
| AVROGEN118 | Error | Code generation failed; the message says why. |

## Requirements

- **To build:** the generator needs the .NET 10 SDK, or Visual Studio 2026 or later, because it runs AvroSharp inside the compiler ([#20](https://github.com/AvroSharp/AvroSharp/issues/20)). Older SDKs cannot load it; [the design notes](design.md) record what fails.
- **To run:** the generated code works on every target AvroSharp supports: .NET 8, 9 and 10, .NET Standard 2.0 and 2.1, and so .NET Framework.
- **C# version:** the generated code needs C# 7.2 or later. The generator reports AVROGEN003 for a project on C# 7.0 or 7.1. For the [command-line tool's](cli.md) `--language-version` and [`CodeGenOptions.LanguageVersion`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.LanguageVersion.html), 7 means C# 7.2 or later.

A build with an older SDK can use the [command-line tool](cli.md) instead, and compile the generated files as ordinary sources.

## Using the generator from source

A package reference is the supported way. A project in the same repository as a build of the generator can reference its project instead, as this repository's samples, tests and benchmarks do ([`build/UseLocalGenerator.targets`](https://github.com/AvroSharp/AvroSharp/blob/main/build/UseLocalGenerator.targets)): as an `Analyzer` project reference, with the generator's own dependencies (`AvroSharp.dll` and `AvroSharp.CodeGen.dll`) added as analyzers, and the package's `build/AvroSharp.Generators.props` and `.targets` imported, which pass the `AvroSharp*` properties to the compiler. The package does all of this itself. [The GeneratorPackage sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratorPackage#building-the-generator-from-source) has the details and caveats.
