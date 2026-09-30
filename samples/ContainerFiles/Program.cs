// Object container files (.avro) with the generic data model: write and read a file, choose a codec, store metadata
// in the header, read asynchronously, and read the file as a newer schema version.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;

var schema = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"PageView","namespace":"analytics","fields":[
      {"name":"timestamp","type":{"type":"long","logicalType":"timestamp-millis"}},
      {"name":"user_id","type":"string"},
      {"name":"url","type":"string"},
      {"name":"referrer","type":["null","string"],"default":null},
      {"name":"country","type":"string"},
      {"name":"duration_ms","type":"int"}]}
    """);

// 5,000 page views, one every 3 seconds from 1 September 2026.
string[] paths = ["/", "/products", "/products/espresso-machine", "/cart", "/checkout", "/blog/cold-brew-guide"];
string[] countries = ["US", "DE", "FR", "GB", "HU", "JP"];
long start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
var pageViews = Enumerable.Range(0, 5000).Select(i => new GenericRecord(schema)
{
    ["timestamp"] = start + (i * 3_000L),
    ["user_id"] = $"user-{(i * 7919) % 400:D4}",
    ["url"] = "https://shop.example.com" + paths[i % paths.Length],
    ["referrer"] = i % 4 == 0 ? "https://www.google.com/" : null,
    ["country"] = countries[(i / 3) % countries.Length],
    ["duration_ms"] = 800 + ((i * 37) % 9000),
}).ToList();

// Write the records to a container file. The writer groups them into blocks and writes the header, with the schema,
// and a sync marker after each block.
using var file = new MemoryStream();
using (var writer = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions { LeaveOpen = true }))
{
    foreach (var pageView in pageViews)
    {
        writer.Write(pageView);
    }
}

byte[] uncompressed = file.ToArray();

// Read it back without knowing the schema: the file carries the schema it was written with.
List<GenericRecord> read;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(uncompressed)))
{
    var written = (RecordSchema)reader.WriterSchema;
    Console.WriteLine($"Writer schema: {written.FullName} with {written.Fields.Count} fields, codec: {reader.Codec.Name}");
    read = reader.ReadAll().Select(value => value.AsRecord()).ToList();
}

Console.WriteLine($"Read {read.Count} page views, the last at {DateTimeOffset.FromUnixTimeMilliseconds(read[^1]["timestamp"].AsInt64()):u}");

// Compress the blocks: deflate is built in, zstandard comes from AvroSharp.Codecs.
byte[] deflated = WriteFile(schema, pageViews, new AvroFileWriterOptions { Codec = DeflateCodec.Default });
byte[] zstandard = WriteFile(schema, pageViews, new AvroFileWriterOptions { Codec = ZstandardCodec.Default });
Console.WriteLine($"Sizes: null {uncompressed.Length:N0} bytes, deflate {deflated.Length:N0}, zstandard {zstandard.Length:N0}");

// A reader of files whose codec is not known in advance gets every codec.
var readerOptions = new AvroFileReaderOptions { Codecs = AvroCodecs.All };
List<GenericRecord> decompressed;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(zstandard), options: readerOptions))
{
    decompressed = reader.ReadAll().Select(value => value.AsRecord()).ToList();
}

// Custom metadata in the header, for example where the file came from. Keys starting with "avro." are reserved.
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

// Asynchronous reading: the stream is read without blocking, and each block is decoded once it is in memory.
long totalDuration = 0;
await using (var reader = await AvroFileReader.OpenGenericAsync(new MemoryStream(zstandard), options: readerOptions))
{
    await foreach (var value in reader.ReadAllAsync())
    {
        totalDuration += value.AsRecord()["duration_ms"].AsInt32();
    }
}

Console.WriteLine($"Total time on page: {TimeSpan.FromMilliseconds(totalDuration).TotalHours:F1} hours");

// Read the file as version 2 of the schema, which drops the referrer and adds a device field with a default.
var schemaV2 = AvroSchema.Parse("""
    {"type":"record","name":"PageView","namespace":"analytics","fields":[
      {"name":"timestamp","type":{"type":"long","logicalType":"timestamp-millis"}},
      {"name":"user_id","type":"string"},
      {"name":"url","type":"string"},
      {"name":"country","type":"string"},
      {"name":"duration_ms","type":"int"},
      {"name":"device","type":"string","default":"unknown"}]}
    """);
GenericRecord first;
using (var reader = AvroFileReader.OpenGeneric(new MemoryStream(uncompressed), readerSchema: schemaV2))
{
    first = reader.ReadAll().First().AsRecord();
    Console.WriteLine($"Read as v2: {first}");
}

var ok = read.SequenceEqual(pageViews)
    && decompressed.SequenceEqual(pageViews)
    && deflated.Length < uncompressed.Length / 2 && zstandard.Length < uncompressed.Length / 2
    && string.Equals(source, "web-frontend", StringComparison.Ordinal)
    && totalDuration == pageViews.Sum(pageView => (long)pageView["duration_ms"].AsInt32())
    && string.Equals(first["device"].AsString(), "unknown", StringComparison.Ordinal)
    && !first.TryGetValue("referrer", out _);
Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;

static byte[] WriteFile(AvroSchema schema, IEnumerable<GenericRecord> records, AvroFileWriterOptions options)
{
    // The writer closes the stream when it is disposed; ToArray still works on a closed MemoryStream.
    var stream = new MemoryStream();
    using (var writer = AvroFileWriter.CreateGeneric(stream, schema, options))
    {
        foreach (var record in records)
        {
            writer.Write(record);
        }
    }

    return stream.ToArray();
}
