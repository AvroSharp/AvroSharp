# Container files

An Avro object container file (`.avro`) holds many records of one schema, in compressed blocks, with the schema in its header. On this page you write page-view events to a file, read them back without knowing the schema, compress the file, and store your own metadata in its header. The examples use the generic data model, so they need no code generation; the full program is the [ContainerFiles sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/ContainerFiles).

The types are in the [`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html) namespace. The records are [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html) values of a `PageView` schema with a timestamp, a user ID, a URL, an optional referrer, a country and a duration. The sample makes 5,000 of them.

## Write a file

[`AvroFileWriter.CreateGeneric`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter.CreateGeneric.html) takes a stream and the schema, and returns an [`AvroFileWriter<AvroValue>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter-1.html). You write records one at a time. The writer groups them into blocks, writes the header with the schema, and writes a sync marker after each block. Disposing the writer writes the last block.

The writer closes the stream when it is disposed, unless [`AvroFileWriterOptions.LeaveOpen`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.LeaveOpen.html) is set, as it is here to keep the `MemoryStream`:

```csharp
using var file = new MemoryStream();
using (var writer = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions { LeaveOpen = true }))
{
    foreach (var pageView in pageViews)
    {
        writer.Write(pageView);
    }
}

byte[] uncompressed = file.ToArray();
```

## Read it back

[`AvroFileReader.OpenGeneric`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.OpenGeneric.html) reads the header when it opens the file. The file carries the schema it was written with, so the reader needs no schema: [`WriterSchema`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.WriterSchema.html) holds it, and [`Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.Codec.html) the codec. [`ReadAll`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.ReadAll.html) returns the records as [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html)s, one block at a time:

```csharp
List<GenericRecord> read;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(uncompressed)))
{
    var written = (RecordSchema)reader.WriterSchema;
    Console.WriteLine($"Writer schema: {written.FullName} with {written.Fields.Count} fields, codec: {reader.Codec.Name}");
    read = reader.ReadAll().Select(value => value.AsRecord()).ToList();
}
```

## Choose a codec

[`AvroFileWriterOptions.Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.Codec.html) compresses each block. The default is `null`, no compression. [`DeflateCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.html) is built in; the AvroSharp.Codecs package adds [`ZstandardCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.ZstandardCodec.html), snappy, bzip2 and xz. `WriteFile` is a helper in the sample that writes the records as above, with the options it is given:

```csharp
byte[] deflated = WriteFile(schema, pageViews, new AvroFileWriterOptions { Codec = DeflateCodec.Default });
byte[] zstandard = WriteFile(schema, pageViews, new AvroFileWriterOptions { Codec = ZstandardCodec.Default });
Console.WriteLine($"Sizes: null {uncompressed.Length:N0} bytes, deflate {deflated.Length:N0}, zstandard {zstandard.Length:N0}");
```

For these 5,000 records it prints `null 325,523 bytes, deflate 54,947, zstandard 56,449`. The ratio depends on the data, so measure with your own.

A reader knows only `null` and `deflate` unless you give it more codecs. A file's codec isn't known until the file is opened, so a reader of files from elsewhere takes them all, with [`AvroCodecs.All`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.AvroCodecs.All.html) in [`AvroFileReaderOptions.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReaderOptions.Codecs.html):

```csharp
var readerOptions = new AvroFileReaderOptions { Codecs = AvroCodecs.All };
List<GenericRecord> decompressed;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(zstandard), options: readerOptions))
{
    decompressed = reader.ReadAll().Select(value => value.AsRecord()).ToList();
}
```

## Store metadata in the header

The header is a map of string keys to byte values. [`AvroFileWriterOptions.Metadata`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.Metadata.html) adds your own entries, such as where the file came from. Keys that start with `avro.` belong to the specification, and the writer rejects them. On the reader, [`TryGetMetadataString`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.TryGetMetadataString.html) decodes an entry as UTF-8, and [`Metadata`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.Metadata.html) holds all of them, including `avro.schema` and `avro.codec`:

```csharp
var metadata = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
{
    ["source"] = Encoding.UTF8.GetBytes("web-frontend"),
    ["export.date"] = Encoding.UTF8.GetBytes("2026-09-01"),
};
byte[] withMetadata = WriteFile(schema, pageViews, new AvroFileWriterOptions { Codec = DeflateCodec.Default, Metadata = metadata });

string? source;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(withMetadata)))
{
    reader.TryGetMetadataString("source", out source);
    Console.WriteLine($"Metadata: source = {source}, keys = {string.Join(", ", reader.Metadata.Keys.Order(StringComparer.Ordinal))}");
}
```

## Read asynchronously

[`OpenGenericAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.OpenGenericAsync.html) and [`ReadAllAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.ReadAllAsync.html) read the stream without blocking, for files on a network share or in blob storage. Each block is decoded synchronously once it is in memory. The writer has [`WriteAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriter-1.WriteAsync.html) and `DisposeAsync` too.

```csharp
long totalDuration = 0;
await using (var reader = await AvroFileReader.OpenGenericAsync(new MemoryStream(zstandard), options: readerOptions))
{
    await foreach (var value in reader.ReadAllAsync())
    {
        totalDuration += value.AsRecord()["duration_ms"].AsInt32();
    }
}
```

## Read as a newer schema version

Pass a reader schema, and the reader resolves each record from the file's schema to it. Here version 2 drops `referrer` and adds a `device` field with a default, so every record read has `device` set to `"unknown"`:

```csharp
GenericRecord first;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(uncompressed), readerSchema: schemaV2))
{
    first = reader.ReadAll().First().AsRecord();
    Console.WriteLine($"Read as v2: {first}");
}
```

The rules for what may change between versions are in [schema evolution](schema-evolution.md).

Generated C# types read and write container files with `AvroFileWriter.Create<T>` and `AvroFileReader.Open<T>`, without the generic model: see the [GeneratedTypes sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratedTypes).

**Next:** [getting started](index.md), [schema evolution](schema-evolution.md), [JSON](json.md), [logical types](logical-types.md), and [code generation](../code-generation.md).
