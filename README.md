# AvroSharp

[![CI](https://github.com/AvroSharp/AvroSharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/AvroSharp/AvroSharp/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Favrosharp.github.io%2FAvroSharp%2Fcoverage.json)](https://github.com/AvroSharp/AvroSharp/actions/workflows/ci.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/AvroSharp?logo=nuget&label=NuGet)](https://www.nuget.org/packages/AvroSharp)
[![Downloads](https://img.shields.io/nuget/dt/AvroSharp?logo=nuget&label=Downloads)](https://www.nuget.org/packages/AvroSharp)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0%20%7C%20netstandard2.0%20%7C%20netstandard2.1-512BD4?logo=dotnet)](#goals)
[![License](https://img.shields.io/github/license/AvroSharp/AvroSharp)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-avrosharp.github.io%2FAvroSharp-blue)](https://avrosharp.github.io/AvroSharp/)

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification.

**[Documentation](https://avrosharp.github.io/AvroSharp/)** · [Getting started](docs/getting-started/index.md) · [Code generation](docs/code-generation.md) · [Command-line tool](docs/cli.md) · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html) · [Benchmarks](docs/benchmarks.md) · [Compared with Apache.Avro](docs/apache-avro.md) · [Migrating from Apache.Avro](docs/migrating-from-apache-avro.md) · [Integrations](docs/integrations.md) · [Samples](samples/README.md)

> **Status:** a release candidate for 1.0.0. The public API is frozen, and from 1.0 it follows [semantic versioning](https://semver.org/): no breaking changes before 2.0. It has:
> - schemas (parsing, writing, canonical form, fingerprints, compatibility checks);
> - binary and JSON encoding of the generic data model, and schema resolution when reading it;
> - C# code generation from `.avsc` files, with a source generator and the `avrosharp` command-line tool, and schema resolution for the generated types; and schemas and serializers for your own C# types with `[AvroSerializable]`;
> - object container files (synchronous and asynchronous) with every codec in the specification;
> - single-object encoding, schema-registry framing (Confluent, Apicurio, AWS Glue), and streams of objects.
>
> Add-on packages for Confluent.Kafka, KafkaFlow, Azure Schema Registry and AWS Glue are planned: see [integrations](docs/integrations.md).
>
> See [the samples](samples/README.md) for runnable examples, [the roadmap](docs/roadmap.md) for what is next, and [the documentation](https://avrosharp.github.io/AvroSharp/) for guides, the API reference, benchmarks and the design.

## Goals

- Complete Avro 1.12 support: schemas, binary and JSON encoding, schema resolution, object container files, single-object encoding, canonical form and fingerprints, and all logical types.
- Faster than Apache.Avro on every scenario in the benchmark suite, with no more allocations. This is a release gate: generated records read 3.95× and write 6.18× faster, and container files read up to 22× faster ([benchmarks](docs/benchmarks.md), [compared with Apache.Avro](docs/apache-avro.md)).
- Serialization code produced by source generators: no reflection, Native AOT and trimming compatible.
- Async-first, low-allocation I/O over `Span<T>`, `IBufferWriter<byte>`, `ReadOnlySequence<byte>` and streams.
- Every codec in the specification (`null`, `deflate`, `snappy`, `bzip2`, `xz`, `zstandard`), implemented with fully managed libraries.
- Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.
- Releases on its own schedule. Apache.Avro, the C# library of the Apache Avro project, ships with the whole project through the Apache release process, which gives every language the same careful, voted releases, a few times a year. As a standalone .NET library, AvroSharp can ship features and fixes as soon as they are ready, and take up users' feature requests quickly. [Feature requests](https://github.com/AvroSharp/AvroSharp/issues) are welcome.

## Getting started

Parse a schema with [`AvroSchema.Parse`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.Parse.html), then write and read values of it with the generic data model ([`AvroSharp.Generic`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html)): [`GenericDatumWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriter.html) and [`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html). [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html) holds any Avro value without boxing, and a [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html) holds a record's fields by name or position.

```csharp
using AvroSharp.Generic;
using AvroSharp.Schemas;

var schema = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"User","namespace":"example","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"}]}
    """);

var user = new GenericRecord(schema) { ["id"] = 1, ["name"] = "Ada" };
byte[] bytes = GenericDatumWriter.Create(schema).WriteToArray(user);

GenericRecord copy = GenericDatumReader.Create(schema).Read(bytes).AsRecord();
string name = copy["name"].AsString();
```

**Schema evolution.** Data written with one version of a schema can be read as another, with a reader made from both schemas ([`GenericDatumReader.Create`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.Create.html)). Fields are matched by name or alias, removed fields are skipped, added fields take their defaults, and numbers are promoted:

```csharp
var v2 = AvroSchema.Parse("""
    {"type":"record","name":"User","namespace":"example","fields":[
      {"name":"id","type":"long"},
      {"name":"name","type":"string"},
      {"name":"active","type":"boolean","default":true}]}
    """);

GenericRecord upgraded = GenericDatumReader.Create(writerSchema: schema, readerSchema: v2).Read(bytes).AsRecord();
bool active = upgraded["active"].AsBoolean();   // true, the default
```

Readers and writers are cached per schema (or schema pair) and are thread-safe, so they can be shared.

**JSON.** [`GenericDatumJsonWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.html) and [`GenericDatumJsonReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonReader.html) implement the specification's JSON encoding, with wrapped union values:

```csharp
string json = GenericDatumJsonWriter.Create(schema).WriteToString(user);   // {"id":1,"name":"Ada"}
AvroValue fromJson = GenericDatumJsonReader.Create(schema).Read(json);
```

For your own types, [generate C# classes from the schema files](#code-generation-from-schema-files): they read and write without the generic model.

## Object container files

[`AvroFileWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.html) writes `.avro` files and [`AvroFileReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.html) reads them, in the [`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html) namespace:

```csharp
using var writer = AvroFileWriter.CreateGeneric(stream, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
writer.Write(record);                        // an AvroValue or GenericRecord

using var reader = AvroFileReader.OpenGeneric(stream, readerSchema);  // readerSchema is optional
foreach (var value in reader.ReadAll()) { ... }

// Asynchronous: no synchronous I/O; each block is decoded synchronously once it is in memory.
await using var asyncReader = await AvroFileReader.OpenGenericAsync(stream);
await foreach (var value in asyncReader.ReadAllAsync(cancellationToken)) { ... }
```

Generated types use their own serializers ([`AvroFileWriter.Create`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.Create.html), [`AvroFileReader.Open`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.Open.html)): on .NET 8 and later `AvroFileWriter.Create<Order>(stream)` and `AvroFileReader.Open<Order>(stream)`, and on every target `AvroFileWriter.Create<Order>(stream, Order.Schema, Order.Write)` and `AvroFileReader.Open<Order>(stream, _ => Order.Read)`. The reader checks every block against the file's sync marker, and limits block sizes ([`AvroFileReaderOptions.MaxBlockLength`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReaderOptions.MaxBlockLength.html)) so a malformed or hostile file cannot make it allocate without bound.

**Codecs.** `null` and `deflate` are built in ([`AvroCodec.Null`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.Null.html), [`AvroCodec.Deflate`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.Deflate.html), or [`DeflateCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.html) with another level). The `AvroSharp.Codecs` package adds the specification's other codecs on fully managed libraries, with no native binaries: [`SnappyCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.SnappyCodec.html), [`ZstandardCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.ZstandardCodec.html), [`Bzip2Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.Bzip2Codec.html) and [`XzCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.XzCodec.html), with [`AvroCodecs.All`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.AvroCodecs.All.html) for readers, in the [`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html) namespace:

```csharp
// Reading: a file's codec is not known until it is opened, so give the reader all of them.
using var reader = AvroFileReader.OpenGeneric(stream, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });

// Writing: pick one, with the same defaults as Apache Avro Java.
var options = new AvroFileWriterOptions { Codec = ZstandardCodec.Default };      // or new ZstandardCodec(level: 9, checksum: true)
```

The codecs are checked against files written by Apache Avro Java, and Java reads the files they write. Other codecs can be plugged in by subclassing [`AvroCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.html).

## Single-object encoding

[`AvroMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.html) writes single-object encoded messages, and [`AvroMessageReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessageReader.html) reads them. An [`IAvroSchemaResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaResolver.html), such as [`AvroSchemaStore`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaStore.html), finds the writer schema by its fingerprint. Both are in the [`AvroSharp.Messages`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.html) namespace.

```csharp
byte[] message = AvroMessage.ToArray(order, Order.Schema, Order.Write);   // C3 01, fingerprint, data

var reader = AvroMessageReader.CreateGeneric(new AvroSchemaStore(v1, v2), readerSchema: v2);
AvroValue value = reader.Read(message);      // the fingerprint selects the writer schema
```

## Schema registries

Messages in a registry's wire framing ([`AvroRegistryFraming`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.html)), with no registry client dependency. [`AvroRegistryMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessage.html) writes them, and the caller supplies the [`AvroSchemaId`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaId.html). [`AvroRegistryMessageReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessageReader.html) reads them, and an [`IAvroSchemaIdResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaIdResolver.html) (a synchronous lookup with an asynchronous fill) finds the schemas.

```csharp
byte[] message = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(42), order, Order.Write);

var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, resolver);
AvroValue value = await reader.ReadAsync(message);   // fetches schema 42 through the resolver the first time
```

| Framing | Header |
|---|---|
| [`Confluent`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.Confluent.html) | `0x00`, 4-byte big-endian ID (byte-identical to Confluent's serializer) |
| [`ConfluentGuid`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.ConfluentGuid.html) | `0x01`, 16-byte big-endian GUID (Confluent Platform 8); [`ConfluentSchemaIdHeader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.ConfluentSchemaIdHeader.html) for the `__value_schema_id` header |
| [`Apicurio`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.Apicurio.html) / [`Apicurio8Byte`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.Apicurio8Byte.html) | `0x00`, 4-byte (Apicurio 3's default) or 8-byte (Apicurio 2's default) big-endian ID |
| [`AwsGlue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.AwsGlue.html) / [`AwsGlueCompressed`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.AwsGlueCompressed.html) | `0x03`, compression byte (`0x00`, or `0x05` for zlib), 16-byte big-endian schema version UUID |

**References.** A schema that refers to named types registered under other subjects parses against them with [`AvroSchemaParser.AddNamedSchemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaParser.AddNamedSchemas.html) (or by parsing the referenced schemas first with the same [`AvroSchemaParser`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaParser.html)). [`schema.ToJson(referencedSchemas)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.ToJson.html) writes it with those types by name, as Java's `Schema.toString(referencedSchemas, false)` does, and `ToJson()` is Java's `Schema.toString()` byte for byte (attribute order, and numbers as Java prints them), so registering AvroSharp's text finds the version a Java client registered.

**Kafka and registry clients.** Add-on packages will plug this into Confluent.Kafka, KafkaFlow, Azure Schema Registry and AWS Glue, starting with `AvroSharp.Confluent`: see [integrations](docs/integrations.md).

## Streams of objects

Objects written one after another with no container or framing, for sockets, pipes or files of concatenated objects. The stream doesn't record the schema, so both sides must know it. [`AvroStreamWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamWriter.html) writes them and [`AvroStreamReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamReader.html) reads them ([`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html)):

```csharp
using (var writer = AvroStreamWriter.Create<Order>(stream, Order.Write))   // or CreateGeneric(stream, schema)
{
    foreach (var order in orders) writer.Write(order);
}

using var reader = AvroStreamReader.OpenGeneric(stream, writerSchema, readerSchema);   // readerSchema is optional
await foreach (var value in reader.ReadAllAsync(cancellationToken)) { ... }
```

Nothing marks where an object ends, so the reader decodes each one to find its end, and reads more when an object runs past its buffer. [`AvroStreamOptions.MaxDatumLength`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamOptions.MaxDatumLength.html) bounds how much it buffers for one object.

## Code generation from schema files

Reference the `AvroSharp.Generators` package and pass your schema files to the compiler. [The code generation guide](docs/code-generation.md) covers every option, the type mapping, diagnostics and moving from avrogen. The engine behind it is the `AvroSharp.CodeGen` package ([`CSharpCodeGenerator`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CSharpCodeGenerator.html)), for your own tools.

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" Version="..." />
  <AdditionalFiles Include="Schemas\*.avsc" />
</ItemGroup>
```

Each named type becomes a C# type:
- a record becomes a `partial class`;
- an enum becomes a C# enum;
- a fixed type becomes a class that wraps exactly its number of bytes.

Each record also gets serializers that call [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html)/[`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) directly, in schema order:

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);

var writer = new AvroWriter(bufferWriter);   // or write into your own IBufferWriter<byte>
shop.Order.Write(ref writer, order);
writer.Flush();

// Without allocating: into caller memory, or into a reused buffer writer.
Span<byte> buffer = stackalloc byte[512];
if (order.TryWriteAvroBytes(buffer, out var written)) { /* buffer[..written] */ }
order.WriteAvroBytes(bufferWriter);

// Reuse one instance for a stream of values: its lists, dictionaries and records are filled again.
var reader = new AvroReader(data);
order.ReadFrom(ref reader);

// On .NET 8 and later, generic code needs no delegates (IAvroSerializable<T>).
byte[] same = AvroSerializer.Serialize(order);
using var file = AvroFileWriter.Create<shop.Order>(stream);
```

- **Cross-file references:** schema files may refer to named types defined in other files. A type repeated identically in several files (as schema sets written for Apache's tooling often do) is generated once.
- **Namespaces:** types without an Avro namespace go into the namespace set by the MSBuild property `AvroSharpNamespace`, or the global namespace. `AvroSharpNamespaceMap` maps Avro namespaces to other C# namespaces, as avrogen's `--namespace` does.
- **Unions:** a union of `null` and one other type becomes a nullable property; other unions become `object?`.
- **Logical types:** `date` becomes `DateOnly` and `time-millis`/`time-micros` become `TimeOnly` (`DateTime`/`TimeSpan` on .NET Framework and netstandard); `timestamp-millis`/`-micros` become `DateTimeOffset` and the `local-` variants `DateTime`; `uuid` becomes `Guid`; `decimal` with a precision up to 28 becomes `decimal`. Decimals are written exactly or rejected, never rounded. Other logical types keep their underlying type. Set `AvroSharpLogicalTypes` to `raw` to keep the underlying types everywhere.
- **Defaults:** `new Order()` gives every field with a schema default its default (primitives, strings, bytes, enums, nullable unions, and arrays and maps of those), as reading data that lacks the field would.
- **Property names:** PascalCase by default (`customer_name` becomes `CustomerName`). Set `AvroSharpPropertyNames` to `avro` to keep the Avro field names as written, as Apache's `avrogen` does (C# keywords are escaped: `@class`), or to `pascal` to force PascalCase.
- **Interfaces:** every record implements [`IAvroSpecificRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSpecificRecord.html), [`IAvroWritable`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroWritable.html) and [`IAvroReadable`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroReadable.html), and on .NET 8 and later [`IAvroSerializable<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html), which [`AvroSerializer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.html), the container and stream readers and writers, and the message helpers accept without delegates.
- **Schema evolution:** `Order.FromAvroBytes(bytes, writerSchema)` reads data written with another version of the schema (added fields take their defaults, removed fields are skipped, numbers are promoted).
- **Field access by position:** every generated record implements `IAvroSpecificRecord` (`Schema`, `Get(int)`, `Put(int, object?)`), following the contract of Apache.Avro's `ISpecificRecord`.
- **Apache.Avro compatibility mode:** set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>` in a project that references Apache.Avro, and the same generated classes also work with Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>`, so code can move to AvroSharp one call site at a time.
  - **What changes:**
    - code written for `avrogen` classes compiles unchanged: property names are avrogen's (the Avro field names) unless `AvroSharpPropertyNames` says otherwise, and types have avrogen's static `_SCHEMA` and instance `Schema` (Apache's `Avro.Schema`); AvroSharp's schema is `AvroSharpSchema`;
    - records also implement `Avro.Specific.ISpecificRecord`;
    - fixed types derive from `Avro.Specific.SpecificFixed`;
    - logical types use Apache's .NET types (`DateTime`, `TimeSpan`, `Guid`, `Avro.AvroDecimal`).
  - **Apache.Avro 1.12.2 limitations** (the tests pin these):
    - its specific writer cannot write a `decimal` on `fixed`;
    - it rejects `uuid` on `fixed`;
    - it reads `local-timestamp` values as UTC instants in local time.
- **Requirements:** the generator needs the .NET 10 SDK or Visual Studio 2026, because it runs AvroSharp inside the compiler. The generated code works on every target AvroSharp supports.

The same package works from C# types too. Mark a `partial` class `[AvroSerializable]`, and its schema and serializers are generated from its members, with the same API as the types above:

```csharp
[AvroSerializable(FieldNames = AvroNaming.CamelCase)]
public partial class Reading
{
    public string Sensor { get; set; } = "";
    public double Value { get; set; }
    public DateTimeOffset TakenAt { get; set; }   // long, timestamp-micros
    public string? Note { get; set; }             // ["null","string"]
}

byte[] bytes = new Reading { Sensor = "t1", Value = 21.5 }.ToAvroBytes();
```

[Code generation](docs/code-generation.md#from-c-types-avroserializable) has the type mapping and the attributes.

## Command-line tool

`avrosharp` is a `dotnet tool`, like Apache.Avro's `avrogen`. It writes the same code as the source generator, for code that is checked in or built outside MSBuild, prints schemas' canonical forms and fingerprints, and checks whether one schema can read another's data:

```shell
dotnet tool install --global AvroSharp.Tool
avrosharp gen schemas/ --output Generated/ --namespace Acme.Events
avrosharp schema canonical user.avsc
avrosharp schema fingerprint user.avsc --algorithm sha256
avrosharp schema compat v1.avsc v2.avsc
```

`gen` takes the generator's options (`--logical-types`, `--property-names`, `--apache-compatible` and others), and writes nothing if a schema is invalid. Errors are in the compiler's format, and the exit code is 0 on success, 1 on failure and 2 for an invalid command line; `schema compat` adds 3 for partially compatible schemas and 4 for incompatible ones. [The tool's documentation](docs/cli.md) has every command and option, examples, and use in CI.

## Benchmarks

Measured against Apache.Avro 1.12.2 with BenchmarkDotNet (i7-12800H, .NET 10, 2026-09-28); every AvroSharp benchmark is faster and allocates no more:

| Area | Faster than Apache.Avro |
|---|---|
| Records, generated code | read 3.95×, write 6.18× |
| Records, generic model | read 1.95×, write 3.93× |
| Schema evolution, generated code | 2.72× |
| Container reads | 2.90–7.36× (null, deflate, snappy, zstandard), up to 22.39× (xz) |
| Container writes | 3.46–10.35× (all codecs but bzip2) |

[The benchmarks page](docs/benchmarks.md) has every area, what is measured, and how to run the suite yourself. [AvroSharp and Apache.Avro](docs/apache-avro.md) covers the other differences, and how to migrate.

## Building

Requires the .NET 10 SDK.

```shell
dotnet build
dotnet test --solution AvroSharp.slnx
```

## License

[MIT](LICENSE).

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
