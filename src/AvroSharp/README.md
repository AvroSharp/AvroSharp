![AvroSharp](https://raw.githubusercontent.com/AvroSharp/AvroSharp/main/docs/images/banner.png)

# AvroSharp

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification: schemas, binary and JSON encoding, schema resolution, object container files and single-object encoding. It is written for spans and `IBufferWriter<byte>`, allocates little, and needs no reflection, so it works with Native AOT and trimming.

> **Status:** a release candidate for 1.0.0. The public API is frozen, and from 1.0 it follows [semantic versioning](https://semver.org/): no breaking changes before 2.0.

**[Documentation](https://avrosharp.github.io/AvroSharp/)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html) · [Code generation](https://avrosharp.github.io/AvroSharp/docs/code-generation.html) · [Benchmarks](https://avrosharp.github.io/AvroSharp/docs/benchmarks.html) · [Compared with Apache.Avro](https://avrosharp.github.io/AvroSharp/docs/apache-avro.html)

Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.

## Getting started

Parse a schema with [`AvroSchema.Parse`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.Parse.html), then write and read values of it with the generic data model: [`GenericDatumWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriter.html) and [`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html). [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html) holds any Avro value without boxing, and a [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html) holds a record's fields by name or position.

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

**Schema evolution.** Data written with one version of a schema can be read as another: [`GenericDatumReader.Create(writerSchema, readerSchema)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.Create.html). Fields are matched by name or alias, removed fields are skipped, added fields take their defaults, and numbers are promoted. Readers and writers are cached per schema (or schema pair) and are thread-safe.

**JSON.** [`GenericDatumJsonWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.html) and [`GenericDatumJsonReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonReader.html) implement the specification's JSON encoding.

## Object container files

[`AvroFileWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.html) writes `.avro` files and [`AvroFileReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.html) reads them:

```csharp
using var writer = AvroFileWriter.CreateGeneric(stream, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate });
writer.Write(user);

using var reader = AvroFileReader.OpenGeneric(stream);
foreach (var value in reader.ReadAll()) { ... }
```

Asynchronous reading and writing are available too ([`OpenGenericAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.OpenGenericAsync.html), [`ReadAllAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.ReadAllAsync.html)). `null` and `deflate` are built in ([`AvroCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.html), [`DeflateCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.html)); the [AvroSharp.Codecs](https://www.nuget.org/packages/AvroSharp.Codecs) package adds snappy, zstandard, bzip2 and xz.

## Single-object encoding, schema registries and streams

[`AvroMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.html) and [`AvroMessageReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessageReader.html) write and read single-object encoded messages, with a schema store ([`AvroSchemaStore`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaStore.html), or your own [`IAvroSchemaResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaResolver.html)) that finds the writer schema by fingerprint. [`AvroRegistryMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessage.html) and [`AvroRegistryMessageReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessageReader.html) write and read messages in a schema registry's framing, with a schema ID instead of the fingerprint ([`AvroRegistryFraming`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.html): Confluent, Apicurio, AWS Glue). [`AvroStreamWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamWriter.html) and [`AvroStreamReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamReader.html) handle a stream of values without a container.

## C# types from schema files

The [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) package generates C# classes from `.avsc` files at build time, with serializers that call [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html)/[`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) directly:

```csharp
byte[] bytes = order.ToAvroBytes();
var copy = shop.Order.FromAvroBytes(bytes);
```

## More

- [Documentation](https://avrosharp.github.io/AvroSharp/) and [samples](https://github.com/AvroSharp/AvroSharp/tree/main/samples)
- [Changelog](https://github.com/AvroSharp/AvroSharp/blob/main/CHANGELOG.md)
- [Third-party notices](https://github.com/AvroSharp/AvroSharp/blob/main/THIRD-PARTY-NOTICES.md) (also in the package)
- Licensed under the [MIT license](https://github.com/AvroSharp/AvroSharp/blob/main/LICENSE).
