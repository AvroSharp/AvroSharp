using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AvroSharp.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Scenarios where AvroSharp is designed to pull furthest ahead of Apache.Avro (generic data model, one record
/// per operation):
/// <list type="bullet">
/// <item><b>Telemetry</b>: a flat record of 24 doubles and a timestamp. Apache.Avro boxes every value it reads.</item>
/// <item><b>Counters</b>: a flat record of 16 small ints (one-byte varints).</item>
/// <item><b>IntArray</b>, <b>LongArray</b>: 1,000 small ints or longs in one array (bulk varint path; on net8+ a Vector128 check decodes runs of one-byte values 16 at a time).</item>
/// <item><b>DoubleArray</b>: 1,000 doubles in one array (bulk copy on little-endian hardware).</item>
/// </list>
/// The numeric flat records are also the baseline for field-run fusion (design section 4.11).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ShowcaseBenchmarks
{
    private GenericDatumWriter _writer = null!;
    private GenericDatumReader _reader = null!;
    private AvroValue _record;
    private Avro.Generic.GenericDatumWriter<ApacheGenericRecord> _apacheWriter = null!;
    private Avro.Generic.GenericDatumReader<ApacheGenericRecord> _apacheReader = null!;
    private ApacheGenericRecord _apacheRecord = null!;
    private byte[] _encoded = [];
    private readonly System.Buffers.ArrayBufferWriter<byte> _output = new(64 * 1024);
    private readonly MemoryStream _stream = new(64 * 1024);

    [Params("Telemetry", "Counters", "IntArray", "LongArray", "DoubleArray")]
    public string Scenario { get; set; } = "Telemetry";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(11);
        var json = SchemaJson(Scenario);
        var schema = (RecordSchema)AvroSchema.Parse(json);
        var record = new GenericRecord(schema);
        switch (Scenario)
        {
            case "Telemetry":
                record[0] = 1_790_000_000_000_000L;
                for (var i = 1; i < schema.Fields.Count; i++)
                {
                    record[i] = (random.NextDouble() * 200) - 100;
                }

                break;
            case "Counters":
                for (var i = 0; i < schema.Fields.Count; i++)
                {
                    record[i] = random.Next(0, 60);
                }

                break;
            case "IntArray":
                record[0] = AvroValue.FromArray(Enumerable.Range(0, 1_000).Select(_ => (AvroValue)random.Next(-60, 60)).ToArray());
                break;
            case "LongArray":
                record[0] = AvroValue.FromArray(Enumerable.Range(0, 1_000).Select(_ => (AvroValue)(long)random.Next(-60, 60)).ToArray());
                break;
            default:
                record[0] = AvroValue.FromArray(Enumerable.Range(0, 1_000).Select(_ => (AvroValue)(random.NextDouble() * 1e6)).ToArray());
                break;
        }

        _record = record;
        _writer = GenericDatumWriter.Create(schema);
        _reader = GenericDatumReader.Create(schema);
        _encoded = _writer.WriteToArray(_record);

        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(json);
        _apacheWriter = new Avro.Generic.GenericDatumWriter<ApacheGenericRecord>(apacheSchema);
        _apacheReader = new Avro.Generic.GenericDatumReader<ApacheGenericRecord>(apacheSchema, apacheSchema);
        _apacheRecord = _apacheReader.Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(_encoded)));
    }

    [GlobalCleanup]
    public void Cleanup() => _stream.Dispose();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Write")]
    public long ApacheAvro_Write()
    {
        _stream.Position = 0;
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        _apacheWriter.Write(_apacheRecord, encoder);
        encoder.Flush();
        return _stream.Position;
    }

    [Benchmark]
    [BenchmarkCategory("Write")]
    public long AvroSharp_Write()
    {
        _output.ResetWrittenCount();
        _writer.Write(_output, _record);
        return _output.WrittenCount;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public object ApacheAvro_Read() =>
        _apacheReader.Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(_encoded, writable: false)));

    [Benchmark]
    [BenchmarkCategory("Read")]
    public AvroValue AvroSharp_Read() => _reader.Read(_encoded);

    private static string SchemaJson(string scenario)
    {
        var fields = new StringBuilder();
        switch (scenario)
        {
            case "Telemetry":
                fields.Append("""{"name":"timestamp","type":"long"}""");
                for (var i = 0; i < 24; i++)
                {
                    fields.Append(CultureInfo.InvariantCulture, $$""",{"name":"sensor{{i}}","type":"double"}""");
                }

                break;
            case "Counters":
                for (var i = 0; i < 16; i++)
                {
                    fields.Append(CultureInfo.InvariantCulture, $$"""{{(i > 0 ? "," : string.Empty)}}{"name":"counter{{i}}","type":"int"}""");
                }

                break;
            case "IntArray":
                fields.Append("""{"name":"values","type":{"type":"array","items":"int"}}""");
                break;
            case "LongArray":
                fields.Append("""{"name":"values","type":{"type":"array","items":"long"}}""");
                break;
            default:
                fields.Append("""{"name":"values","type":{"type":"array","items":"double"}}""");
                break;
        }

        return $$"""{"type":"record","name":"{{scenario}}","namespace":"bench","fields":[{{fields}}]}""";
    }
}
