# AvroSharp

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification: schemas, binary and JSON encoding, schema resolution, object container files and single-object encoding. It is written for spans and `IBufferWriter<byte>`, allocates little, and needs no reflection, so it works with Native AOT and trimming.

> **Status:** an early preview (0.x). The API may still change before 1.0.

**[Documentation](https://zcsizmadia.github.io/AvroSharp/)** · [API reference](https://zcsizmadia.github.io/AvroSharp/docs/api/index.html) · [Code generation](https://zcsizmadia.github.io/AvroSharp/docs/code-generation.html) · [Benchmarks](https://zcsizmadia.github.io/AvroSharp/docs/benchmarks.html) · [Compared with Apache.Avro](https://zcsizmadia.github.io/AvroSharp/docs/apache-avro.html)

Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.

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
```

**Schema evolution.** Data written with one version of a schema can be read as another: `GenericDatumReader.Create(writerSchema, readerSchema)`. Fields are matched by name or alias, removed fields are skipped, added fields take their defaults, and numbers are promoted. Readers and writers are cached per schema (or schema pair) and are thread-safe.

**JSON.** `GenericDatumJsonWriter` and `GenericDatumJsonReader` implement the specification's JSON encoding.

## Object container files

```csharp
using var writer = AvroFileWriter.CreateGeneric(stream, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
writer.Write(user);

using var reader = AvroFileReader.OpenGeneric(stream);
foreach (var value in reader.ReadAll()) { ... }
```

Asynchronous reading and writing are available too (`OpenGenericAsync`, `ReadAllAsync`). `null` and `deflate` are built in; the [AvroSharp.Codecs](https://www.nuget.org/packages/AvroSharp.Codecs) package adds snappy, zstandard, bzip2 and xz.

## Single-object encoding and streams

`AvroMessage` and `AvroMessageReader` write and read single-object encoded messages, with a schema store that finds the writer schema by fingerprint. `AvroStreamWriter` and `AvroStreamReader` handle a stream of values without a container.

## C# types from schema files

The [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) package generates C# classes from `.avsc` files at build time, with serializers that call `AvroWriter`/`AvroReader` directly:

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);
```

## More

- [Documentation](https://zcsizmadia.github.io/AvroSharp/) and [samples](https://github.com/zcsizmadia/AvroSharp/tree/main/samples)
- [Changelog](https://github.com/zcsizmadia/AvroSharp/blob/main/CHANGELOG.md)
- [Third-party notices](https://github.com/zcsizmadia/AvroSharp/blob/main/THIRD-PARTY-NOTICES.md) (also in the package)
- Licensed under the [MIT license](https://github.com/zcsizmadia/AvroSharp/blob/main/LICENSE).
