using System;
using System.IO;
using System.Linq;
using System.Text;
using AvroSharp;
using AvroSharp.Schemas;

// Native AOT smoke test: exercises the public API from a trimmed, AOT-compiled executable.
// Grows with each milestone; the exit code is the test result.
var failures = 0;

void Check(bool condition, string what)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAIL: {what}");
        failures++;
    }
}

foreach (var name in new[] { AvroCodecNames.Null, AvroCodecNames.Deflate, AvroCodecNames.Snappy, AvroCodecNames.Bzip2, AvroCodecNames.Xz, AvroCodecNames.Zstandard })
{
    Check(AvroCodecNames.IsStandard(name), $"'{name}' recognised as a standard codec");
}

// Schema parsing, canonical form and fingerprint (vector 033 of Apache Avro's schema-tests.txt).
const string pig = """{"name":"PigValue","type":"record","fields":[{"name":"value", "type":["null", "int", "long", "PigValue"]}]}""";
var schema = AvroSchema.Parse(pig);
Check(Same(schema.CanonicalForm, """{"name":"PigValue","type":"record","fields":[{"name":"value","type":["null","int","long","PigValue"]}]}"""), "canonical form");
Check(schema.Fingerprint64 == -1759257747318642341, "CRC-64-AVRO fingerprint");
Check(SchemaFingerprint.Sha256(schema).Length == 32, "SHA-256 fingerprint");

// Full JSON round trip, UTF-8 and async parsing, and error locations.
const string rich = """
    {"type":"record","name":"Order","namespace":"shop","fields":[
      {"name":"id","type":{"type":"string","logicalType":"uuid"}},
      {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
      {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID"]},"default":"NEW"}]}
    """;
var order = (RecordSchema)AvroSchema.Parse(rich);
Check(Same(AvroSchema.Parse(order.ToJson()).ToJson(), order.ToJson()), "JSON round trip");
Check(order.GetField("total").Schema.LogicalType is DecimalLogicalType { Precision: 12, Scale: 2 }, "decimal logical type");
Check(Same(AvroSchema.Parse(Encoding.UTF8.GetBytes(rich)).CanonicalForm, order.CanonicalForm), "UTF-8 parsing");
using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(rich)))
{
    Check(Same((await AvroSchema.ParseAsync(stream).ConfigureAwait(false)).CanonicalForm, order.CanonicalForm), "async parsing");
}

try
{
    AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"Missing"}]}""");
    Check(false, "undefined name rejected");
}
catch (AvroSchemaException ex)
{
    Check(Same(ex.Path, "$.fields[0].type") && ex.LineNumber == 1, "error path and line");
}

// Binary encoding and the generic data model.
var person = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Person","fields":[
      {"name":"name","type":"string"},{"name":"age","type":"int"},
      {"name":"scores","type":{"type":"array","items":"long"}},{"name":"email","type":["null","string"]}]}
    """);
var alice = new AvroSharp.Generic.GenericRecord(person)
{
    ["name"] = "Alice",
    ["age"] = 30,
    ["scores"] = AvroSharp.Generic.AvroValue.FromArray(new AvroSharp.Generic.AvroValue[] { 1L, 200L, -3L }),
    ["email"] = AvroSharp.Generic.AvroValue.Null,
};
var encoded = AvroSharp.Generic.GenericDatumWriter.Create(person).WriteToArray(alice);
var decoded = AvroSharp.Generic.GenericDatumReader.Create(person).Read(encoded);
Check(decoded.Equals((AvroSharp.Generic.AvroValue)alice), "generic record round trip");
Check(Same(Convert.ToHexString(encoded), "0A416C6963653C06029003050000"), "generic record bytes");

// Object container files with every codec: the built-in ones and those of AvroSharp.Codecs.
foreach (var codec in new[] { AvroSharp.Containers.AvroCodec.Null, AvroSharp.Containers.AvroCodec.Deflate }.Concat(AvroSharp.Codecs.AvroCodecs.All))
{
    using var file = new MemoryStream();
    using (var writer = AvroSharp.Containers.AvroFileWriter.CreateGeneric(file, person, new AvroSharp.Containers.AvroFileWriterOptions { Codec = codec, SyncInterval = 16, LeaveOpen = true }))
    {
        for (var i = 0; i < 10; i++)
        {
            writer.Write(alice);
        }
    }

    file.Position = 0;
    using var reader = AvroSharp.Containers.AvroFileReader.OpenGeneric(file, options: new AvroSharp.Containers.AvroFileReaderOptions { Codecs = AvroSharp.Codecs.AvroCodecs.All });
    var count = 0;
    foreach (var record in reader.ReadAll())
    {
        count += record.Equals((AvroSharp.Generic.AvroValue)alice) ? 1 : 0;
    }

    Check(count == 10 && Same(reader.Codec.Name, codec.Name), $"container file with the {codec.Name} codec");
}

// Single-object encoding.
var message = AvroSharp.Messages.AvroMessage.ToArray(alice, AvroSharp.Generic.GenericDatumWriter.Create(person));
var fromMessage = AvroSharp.Messages.AvroMessageReader.CreateGeneric(new AvroSharp.Messages.AvroSchemaStore(person)).Read(message);
Check(fromMessage.Equals((AvroSharp.Generic.AvroValue)alice) && message.Length == encoded.Length + AvroSharp.Messages.AvroMessage.HeaderLength, "single-object encoding");

// Generated types: IAvroSerializable<T> through AvroSerializer and the container file, writing into caller memory,
// and reading into a reused instance.
var reading = new smoke.Reading { Sensor = "t1", Value = 21.5, Samples = { 1, 2, 3 }, Tags = { ["room"] = "lab" }, Next = new smoke.Reading { Sensor = "t2" } };
var readingBytes = AvroSharp.Serialization.AvroSerializer.Serialize(reading);
Check(AvroSharp.Serialization.AvroSerializer.Deserialize<smoke.Reading>(readingBytes).ToAvroBytes().AsSpan().SequenceEqual(readingBytes), "generated type round trip");
Check(new smoke.Reading().Unit == smoke.Unit.C && new smoke.Reading().Samples.Count == 0, "generated defaults");
Span<byte> stackBuffer = stackalloc byte[128];
Check(reading.TryWriteAvroBytes(stackBuffer, out var readingWritten) && stackBuffer[..readingWritten].SequenceEqual(readingBytes), "writing into caller memory");
Check(!reading.TryWriteAvroBytes(stackBuffer[..4], out _), "reporting that a value does not fit");
var reused = new smoke.Reading();
var readingReader = new AvroSharp.IO.AvroReader(readingBytes);
reused.ReadFrom(ref readingReader);
Check(reused.ToAvroBytes().AsSpan().SequenceEqual(readingBytes), "reading into a reused instance");
using (var readingFile = new MemoryStream())
{
    using (var writer = AvroSharp.Containers.AvroFileWriter.Create<smoke.Reading>(readingFile, new AvroSharp.Containers.AvroFileWriterOptions { Codec = AvroSharp.Codecs.ZstandardCodec.Default, LeaveOpen = true }))
    {
        writer.Write(reading);
    }

    readingFile.Position = 0;
    using var reader = AvroSharp.Containers.AvroFileReader.Open<smoke.Reading>(readingFile, new AvroSharp.Containers.AvroFileReaderOptions { Codecs = AvroSharp.Codecs.AvroCodecs.All });
    Check(reader.ReadAll().Single().ToAvroBytes().AsSpan().SequenceEqual(readingBytes), "container file of a generated type");
}

static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

Console.WriteLine(failures == 0 ? "AOT smoke test passed" : $"AOT smoke test failed ({failures})");
return failures == 0 ? 0 : 1;
