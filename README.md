# AvroSharp

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification.

> **Status:** early development, not yet released. Working today:
> - schemas (parsing, writing, canonical form, fingerprints);
> - binary and JSON encoding of the generic data model, and schema resolution when reading it;
> - C# code generation from `.avsc` files;
> - object container files (synchronous and asynchronous) with every codec in the specification;
> - single-object encoding.
>
> See [the design](docs/design.md) for the roadmap.

## Goals

- Complete Avro 1.12 support: schemas, binary and JSON encoding, schema resolution, object container files, single-object encoding, canonical form and fingerprints, and all logical types.
- Faster than Apache.Avro on every scenario in the benchmark suite, with fewer allocations. This is a release gate; results will be published once the benchmarks exist.
- Serialization code produced by source generators: no reflection, Native AOT and trimming compatible.
- Async-first, low-allocation I/O over `Span<T>`, `IBufferWriter<byte>`, `ReadOnlySequence<byte>` and `System.IO.Pipelines`.
- Every codec in the specification (`null`, `deflate`, `snappy`, `bzip2`, `xz`, `zstandard`), implemented with fully managed libraries.
- Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.

## Getting started

Parse a schema, then write and read values of it with the generic data model. `AvroValue` holds any Avro value without boxing, and a `GenericRecord` holds a record's fields by name or position.

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

**Schema evolution.** Data written with one version of a schema can be read as another. Fields are matched by name or alias, removed fields are skipped, added fields take their defaults, and numbers are promoted:

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

**JSON.** The specification's JSON encoding, with wrapped union values:

```csharp
string json = GenericDatumJsonWriter.Create(schema).WriteToString(user);   // {"id":1,"name":"Ada"}
AvroValue fromJson = GenericDatumJsonReader.Create(schema).Read(json);
```

For your own types, [generate C# classes from the schema files](#code-generation-from-schema-files): they read and write without the generic model.

## Object container files

```csharp
using var writer = AvroFileWriter.CreateGeneric(stream, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
writer.Write(record);                        // an AvroValue or GenericRecord

using var reader = AvroFileReader.OpenGeneric(stream, readerSchema);  // readerSchema is optional
foreach (var value in reader.ReadAll()) { ... }

// Asynchronous: no synchronous I/O; each block is decoded synchronously once it is in memory.
await using var asyncReader = await AvroFileReader.OpenGenericAsync(stream);
await foreach (var value in asyncReader.ReadAllAsync(cancellationToken)) { ... }
```

Generated types use their own serializers: `AvroFileWriter.Create<Order>(stream, Order.Schema, Order.Write)` and `AvroFileReader.Open<Order>(stream, _ => Order.Read)`. The reader checks every block against the file's sync marker, and limits block sizes (`AvroFileReaderOptions.MaxBlockLength`) so a malformed or hostile file cannot make it allocate without bound.

**Codecs.** `null` and `deflate` are built in. The `AvroSharp.Codecs` package adds the specification's other codecs (snappy, zstandard, bzip2 and xz) on fully managed libraries, with no native binaries:

```csharp
// Reading: a file's codec is not known until it is opened, so give the reader all of them.
using var reader = AvroFileReader.OpenGeneric(stream, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });

// Writing: pick one, with the same defaults as Apache Avro Java.
var options = new AvroFileWriterOptions { Codec = ZstandardCodec.Default };      // or new ZstandardCodec(level: 9, checksum: true)
```

The codecs are checked against files written by Apache Avro Java, and Java reads the files they write. Other codecs can be plugged in by subclassing `AvroCodec`.

## Single-object encoding

```csharp
byte[] message = AvroMessage.ToArray(order, Order.Schema, Order.Write);   // C3 01, fingerprint, data

var reader = AvroMessageReader.CreateGeneric(new AvroSchemaStore(v1, v2), readerSchema: v2);
AvroValue value = reader.Read(message);      // the fingerprint selects the writer schema
```

## Schema registries

Messages in a registry's wire framing, with no registry client dependency: the caller supplies the schema ID, and an `IAvroSchemaIdResolver` (a synchronous lookup with an asynchronous fill) finds schemas when reading.

```csharp
byte[] message = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(42), order, Order.Write);

var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, resolver);
AvroValue value = await reader.ReadAsync(message);   // fetches schema 42 through the resolver the first time
```

| Framing | Header |
|---|---|
| `Confluent` | `0x00`, 4-byte big-endian ID (byte-identical to Confluent's serializer) |
| `ConfluentGuid` | `0x01`, 16-byte big-endian GUID (Confluent Platform 8); `ConfluentSchemaIdHeader` for the `__value_schema_id` header |
| `Apicurio` / `Apicurio8Byte` | `0x00`, 4-byte (Apicurio 3's default) or 8-byte (Apicurio 2's default) big-endian ID |
| `AwsGlue` / `AwsGlueCompressed` | `0x03`, compression byte (`0x00`, or `0x05` for zlib), 16-byte big-endian schema version UUID |

**References.** A schema that refers to named types registered under other subjects parses against them with `AvroSchemaParser.AddNamedSchemas` (or by parsing the referenced schemas first with the same parser). `schema.ToJson(referencedSchemas)` writes it with those types by name, as Java's `Schema.toString(referencedSchemas, false)` does, and `ToJson()` is Java's `Schema.toString()` byte for byte (attribute order, and numbers as Java prints them), so registering AvroSharp's text finds the version a Java client registered.

## Streams of objects

Objects written one after another with no container or framing, for sockets, pipes or files of concatenated objects. The stream doesn't record the schema, so both sides must know it:

```csharp
using (var writer = AvroStreamWriter.Create<Order>(stream, Order.Write))   // or CreateGeneric(stream, schema)
{
    foreach (var order in orders) writer.Write(order);
}

using var reader = AvroStreamReader.OpenGeneric(stream, writerSchema, readerSchema);   // readerSchema is optional
await foreach (var value in reader.ReadAllAsync(cancellationToken)) { ... }
```

Nothing marks where an object ends, so the reader decodes each one to find its end, and reads more when an object runs past its buffer. `AvroStreamOptions.MaxDatumLength` bounds how much it buffers for one object.

## Code generation from schema files

Reference the `AvroSharp.Generators` package and pass your schema files to the compiler:

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

Each record also gets serializers that call `AvroWriter`/`AvroReader` directly, in schema order:

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);

var writer = new AvroWriter(bufferWriter);   // or write into your own IBufferWriter<byte>
shop.Order.Write(ref writer, order);
writer.Flush();
```

- **Cross-file references:** schema files may refer to named types defined in other files. A type repeated identically in several files (as schema sets written for Apache's tooling often do) is generated once.
- **Namespaces:** types without an Avro namespace go into the namespace set by the MSBuild property `AvroSharpNamespace`, or the global namespace.
- **Unions:** a union of `null` and one other type becomes a nullable property; other unions become `object?`.
- **Logical types:** `date` becomes `DateOnly` and `time-millis`/`time-micros` become `TimeOnly` (`DateTime`/`TimeSpan` on .NET Framework and netstandard); `timestamp-millis`/`-micros` become `DateTimeOffset` and the `local-` variants `DateTime`; `uuid` becomes `Guid`; `decimal` with a precision up to 28 becomes `decimal`. Decimals are written exactly or rejected, never rounded. Other logical types keep their underlying type. Set `AvroSharpLogicalTypes` to `raw` to keep the underlying types everywhere.
- **Property names:** PascalCase by default (`customer_name` becomes `CustomerName`). Set `AvroSharpPropertyNames` to `avro` to keep the Avro field names as written, as Apache's `avrogen` does (C# keywords are escaped: `@class`).
- **Schema evolution:** `Order.FromAvroBytes(bytes, writerSchema)` reads data written with another version of the schema (added fields take their defaults, removed fields are skipped, numbers are promoted).
- **Field access by position:** every generated record implements `IAvroSpecificRecord` (`Schema`, `Get(int)`, `Put(int, object?)`), following the contract of Apache.Avro's `ISpecificRecord`.
- **Apache.Avro compatibility mode:** set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>` in a project that references Apache.Avro, and the same generated classes also work with Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>`, so code can move to AvroSharp one call site at a time.
  - **What changes:**
    - records also implement `Avro.Specific.ISpecificRecord`;
    - fixed types derive from `Avro.Specific.SpecificFixed`;
    - logical types use Apache's .NET types (`DateTime`, `TimeSpan`, `Guid`, `Avro.AvroDecimal`).
  - **Apache.Avro 1.12.2 limitations** (the tests pin these):
    - its specific writer cannot write a `decimal` on `fixed`;
    - it rejects `uuid` on `fixed`;
    - it reads `local-timestamp` values as UTC instants in local time.
- **Requirements:** the generator needs the .NET 10 SDK or Visual Studio 2026, because it runs AvroSharp inside the compiler. The generated code works on every target AvroSharp supports.

## Building

Requires the .NET 10 SDK.

```shell
dotnet build
dotnet test --solution AvroSharp.slnx
```

## License

[MIT](LICENSE).

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
