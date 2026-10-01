# Getting started

AvroSharp reads and writes [Apache Avro™](https://avro.apache.org/) data in .NET. This section starts with a first program, then covers the tasks most applications need, one page each. Every page's code is taken from a sample in [`samples/`](https://github.com/AvroSharp/AvroSharp/tree/main/samples), which CI builds and runs, so it compiles and does what the page says.

## Install

For the generic data model, schemas, container files and messages:

```shell
dotnet add package AvroSharp
```

For C# types generated from your `.avsc` files as the project builds (it brings in `AvroSharp`):

```shell
dotnet add package AvroSharp.Generators
```

The snappy, zstandard, bzip2 and xz codecs for container files are in `AvroSharp.Codecs`. The packages support .NET 8, 9 and 10, .NET Standard 2.0 and 2.1, and .NET Framework. The source generator needs the .NET 10 SDK or Visual Studio 2026 to build, and the code it generates runs on all of them.

## A first program

An Avro schema is JSON. [`AvroSchema.Parse`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.Parse.html) reads it into a [`RecordSchema`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.RecordSchema.html) here:

```csharp
var userV1 = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"User","namespace":"example","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"email","type":["null","string"],"default":null}]}
    """);
```

Without generated types, a record is a [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html), whose fields are set by name. [`GenericDatumWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriter.html) writes it in Avro's binary encoding, and [`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html) reads it back as an [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html):

```csharp
var ada = new GenericRecord(userV1) { ["id"] = 1, ["name"] = "Ada", ["email"] = "ada@example.com" };
byte[] bytes = GenericDatumWriter.Create(userV1).WriteToArray(ada);
GenericRecord copy = GenericDatumReader.Create(userV1).Read(bytes).AsRecord();
```

`Create` returns a reader or writer cached for the schema. They are thread-safe, so an application keeps one per schema and shares it.

Data written with one version of a schema can be read as another. Here version 2 widens `id`, drops `email` and adds `active` with a default:

```csharp
GenericRecord upgraded = GenericDatumReader.Create(writerSchema: userV1, readerSchema: userV2).Read(bytes).AsRecord();
```

[Schema evolution](schema-evolution.md) explains the rules. A schema's [fingerprint](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.Fingerprint64.html) identifies it, for comparing schemas and in schema stores:

```csharp
Console.WriteLine($"Fingerprint: 0x{userV1.Fingerprint64:X16}");
```

The whole program is the [GettingStarted sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GettingStarted).

## Next

- [Generated types](generated-types.md): C# classes from `.avsc` files, with serializers that use no reflection.
- [Container files](containers.md): writing and reading `.avro` files, with codecs.
- [Schema evolution](schema-evolution.md): reading old data with a newer schema.
- [Logical types](logical-types.md): dates, timestamps, decimals and UUIDs.
- [JSON](json.md): Avro's JSON encoding.
- Messages and schema registries: the [guide's sections](../../README.md#single-object-encoding), and the [Messaging sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/Messaging).
- [Migrating from Apache.Avro](../migrating-from-apache-avro.md), and the [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html).
