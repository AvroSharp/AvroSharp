# AvroSharp

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification.

> **Status:** early development, not yet released. Working today:
> - schemas (parsing, writing, canonical form, fingerprints);
> - binary and JSON encoding of the generic data model, and schema resolution when reading it;
> - C# code generation from `.avsc` files;
> - object container files with the `null` and `deflate` codecs.
>
> Not implemented yet: the other codecs, async container I/O and single-object encoding. See [the design](docs/design.md) for the roadmap.

## Goals

- Complete Avro 1.12 support: schemas, binary and JSON encoding, schema resolution, object container files, single-object encoding, canonical form and fingerprints, and all logical types.
- Faster than Apache.Avro on every scenario in the benchmark suite, with fewer allocations. This is a release gate; results will be published once the benchmarks exist.
- Serialization code produced by source generators: no reflection, Native AOT and trimming compatible.
- Async-first, low-allocation I/O over `Span<T>`, `IBufferWriter<byte>`, `ReadOnlySequence<byte>` and `System.IO.Pipelines`.
- Every codec in the specification (`null`, `deflate`, `snappy`, `bzip2`, `xz`, `zstandard`), implemented with fully managed libraries.
- Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.

## Object container files

```csharp
using var writer = AvroFileWriter.CreateGeneric(stream, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
writer.Write(record);                        // an AvroValue or GenericRecord

using var reader = AvroFileReader.OpenGeneric(stream, readerSchema);  // readerSchema is optional
foreach (var value in reader.ReadAll()) { ... }
```

Generated types use their own serializers: `AvroFileWriter.Create<Order>(stream, Order.Schema, Order.Write)` and `AvroFileReader.Open<Order>(stream, _ => Order.Read)`. The reader checks every block against the file's sync marker, and limits block sizes (`AvroFileReaderOptions.MaxBlockLength`) so a malformed or hostile file cannot make it allocate without bound. Other codecs can be plugged in by subclassing `AvroCodec`.

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
