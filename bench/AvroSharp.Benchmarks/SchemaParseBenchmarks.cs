using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Schema parsing, and parsing followed by the CRC-64-AVRO fingerprint (what a schema registry client does).
/// Apache.Avro is the baseline of each category; Chr.Avro is reported for reference only.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SchemaParseBenchmarks
{
    private const string SmallSchema = """
        {"type":"record","name":"User","namespace":"com.example","doc":"A user","fields":[
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"name","type":"string"},
          {"name":"email","type":["null","string"],"default":null},
          {"name":"age","type":"int","default":0},
          {"name":"created","type":{"type":"long","logicalType":"timestamp-micros"}},
          {"name":"role","type":{"type":"enum","name":"Role","symbols":["ADMIN","USER","GUEST"]}},
          {"name":"tags","type":{"type":"array","items":"string"},"default":[]}
        ]}
        """;

    private string _json = string.Empty;

    [Params("Small", "Large")]
    public string Schema { get; set; } = "Small";

    [GlobalSetup]
    public void Setup() => _json = string.Equals(Schema, "Small", System.StringComparison.Ordinal) ? SmallSchema : LargeSchema(recordCount: 40, fieldsPerRecord: 8);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Parse")]
    public object ApacheAvro_Parse() => Avro.Schema.Parse(_json);

    [Benchmark]
    [BenchmarkCategory("Parse")]
    public object AvroSharp_Parse() => AvroSchema.Parse(_json);

    [Benchmark]
    [BenchmarkCategory("Parse", Gate.ReferenceOnly)]
    public object ChrAvro_Parse() => new Chr.Avro.Representation.JsonSchemaReader().Read(_json);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ParseAndFingerprint")]
    public long ApacheAvro_ParseAndFingerprint() => Avro.SchemaNormalization.ParsingFingerprint64(Avro.Schema.Parse(_json));

    [Benchmark]
    [BenchmarkCategory("ParseAndFingerprint")]
    public long AvroSharp_ParseAndFingerprint() => AvroSchema.Parse(_json).Fingerprint64;

    /// <summary>
    /// A deterministic, wide schema: a root record whose fields are records with enums, unions, arrays, maps,
    /// fixed and logical types. Kept shallow because Apache.Avro's JSON reader limits nesting depth.
    /// </summary>
    internal static string LargeSchema(int recordCount, int fieldsPerRecord)
    {
        var json = new StringBuilder("""{"type":"record","name":"Root","namespace":"com.example.large","doc":"Many records","fields":[""");
        for (var r = 0; r < recordCount; r++)
        {
            json.Append(r > 0 ? "," : string.Empty);
            json.Append(CultureInfo.InvariantCulture, $$"""{"name":"record{{r}}","type":{"type":"record","name":"Record{{r}}","doc":"Record number {{r}}","fields":[""");
            for (var f = 0; f < fieldsPerRecord; f++)
            {
                var type = (f % 8) switch
                {
                    0 => "\"long\"",
                    1 => "[\"null\",\"string\"]",
                    2 => """{"type":"long","logicalType":"timestamp-millis"}""",
                    3 => $$"""{"type":"enum","name":"Enum{{r}}_{{f}}","symbols":["A","B","C","D"]}""",
                    4 => """{"type":"array","items":"double"}""",
                    5 => """{"type":"map","values":["null","long"]}""",
                    6 => $$"""{"type":"fixed","name":"Fixed{{r}}_{{f}}","size":16}""",
                    _ => """{"type":"bytes","logicalType":"decimal","precision":18,"scale":4}""",
                };
                json.Append(f > 0 ? "," : string.Empty);
                json.Append(CultureInfo.InvariantCulture, $$"""{"name":"field{{f}}","type":{{type}},"doc":"Field {{f}}"}""");
            }

            json.Append("]}}");
        }

        return json.Append("]}").ToString();
    }
}
