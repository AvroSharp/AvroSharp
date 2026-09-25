using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvroSharp.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Writing and reading one record with the generic data model: AvroSharp's GenericDatumWriter/Reader versus
/// Apache.Avro's GenericDatumWriter/Reader, on the same schema and the same logical data.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GenericRecordBenchmarks
{
    private const string OrderJson = """
        {"type":"record","name":"Order","namespace":"bench","fields":[
          {"name":"id","type":"long"},
          {"name":"customer","type":"string"},
          {"name":"total","type":"double"},
          {"name":"quantity","type":"int"},
          {"name":"paid","type":"boolean"},
          {"name":"note","type":["null","string"]},
          {"name":"lines","type":{"type":"array","items":{"type":"record","name":"Line","fields":[
            {"name":"sku","type":"string"},{"name":"qty","type":"int"},{"name":"price","type":"double"}]}}},
          {"name":"counters","type":{"type":"array","items":"long"}},
          {"name":"tags","type":{"type":"map","values":"string"}}
        ]}
        """;

    private GenericDatumWriter _writer = null!;
    private GenericDatumReader _reader = null!;
    private AvroValue _record;
    private Avro.Generic.GenericDatumWriter<ApacheGenericRecord> _apacheWriter = null!;
    private Avro.Generic.GenericDatumReader<ApacheGenericRecord> _apacheReader = null!;
    private ApacheGenericRecord _apacheRecord = null!;
    private byte[] _encoded = [];
    private readonly System.Buffers.ArrayBufferWriter<byte> _output = new();
    private readonly MemoryStream _stream = new();

    [GlobalSetup]
    public void Setup()
    {
        var schema = (RecordSchema)AvroSchema.Parse(OrderJson);
        _writer = GenericDatumWriter.Create(schema);
        _reader = GenericDatumReader.Create(schema);

        var line = (RecordSchema)((AvroSharp.Schemas.ArraySchema)schema.GetField("lines").Schema).Items;
        _record = new GenericRecord(schema)
        {
            ["id"] = 1_790_000_000_123L,
            ["customer"] = "Customer 42",
            ["total"] = 1234.56,
            ["quantity"] = 12,
            ["paid"] = true,
            ["note"] = "Leave at the door",
            ["lines"] = AvroValue.FromArray(Enumerable.Range(0, 10)
                .Select(i => (AvroValue)new GenericRecord(line) { ["sku"] = $"SKU-{i:0000}", ["qty"] = i + 1, ["price"] = 9.99 * i })
                .ToList()),
            ["counters"] = AvroValue.FromArray(Enumerable.Range(0, 64).Select(i => (AvroValue)(long)(i % 50)).ToList()),
            ["tags"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(System.StringComparer.Ordinal) { ["channel"] = "web", ["region"] = "eu" }),
        };

        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(OrderJson);
        _apacheWriter = new Avro.Generic.GenericDatumWriter<ApacheGenericRecord>(apacheSchema);
        _apacheReader = new Avro.Generic.GenericDatumReader<ApacheGenericRecord>(apacheSchema, apacheSchema);
        _encoded = _writer.WriteToArray(_record);
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
}
