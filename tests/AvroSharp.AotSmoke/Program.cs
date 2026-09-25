using System;
using System.IO;
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

static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

Console.WriteLine(failures == 0 ? "AOT smoke test passed" : $"AOT smoke test failed ({failures})");
return failures == 0 ? 0 : 1;
