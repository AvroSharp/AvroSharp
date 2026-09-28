using System.IO;
using AvroSharp.Generic;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// A wide record (Schemas/wide.avsc: 140 fields, mostly optional primitives, half of them null, and a few int arrays),
/// the shape of many production event schemas. Generated serializers of records this wide are split into methods of
/// 32 fields, which keeps each within the JIT's limits (#113).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WideRecordBenchmarks
{
    private GenericDatumWriter _writer = null!;
    private GenericDatumReader _reader = null!;
    private AvroValue _record;
    private Avro.Generic.GenericDatumWriter<ApacheGenericRecord> _apacheWriter = null!;
    private Avro.Generic.GenericDatumReader<ApacheGenericRecord> _apacheReader = null!;
    private ApacheGenericRecord _apacheRecord = null!;
    private bench.generated.Wide _generated = null!;
    private byte[] _encoded = [];
    private readonly System.Buffers.ArrayBufferWriter<byte> _output = new();
    private readonly MemoryStream _stream = new();

    [GlobalSetup]
    public void Setup()
    {
        var schema = (RecordSchema)bench.generated.Wide.Schema;
        _writer = GenericDatumWriter.Create(schema);
        _reader = GenericDatumReader.Create(schema);

        // Every other optional field is null; values vary by field so varints have mixed lengths.
        var record = new GenericRecord(schema);
        for (var i = 0; i < schema.Fields.Count; i++)
        {
            var field = schema.Fields[i];
            var type = field.Schema is AvroSharp.Schemas.UnionSchema union ? union.Branches[1] : field.Schema;
            if (field.Schema is AvroSharp.Schemas.UnionSchema && i % 2 == 0)
            {
                record[i] = AvroValue.Null;
                continue;
            }

            record[i] = type switch
            {
                AvroSharp.Schemas.ArraySchema => AvroValue.FromInt32Array(new[] { i, i * 100, i * 10_000 }),
                _ => type.Type switch
                {
                    AvroSharp.Schemas.AvroSchemaType.Int => (AvroValue)(i * 997),
                    AvroSharp.Schemas.AvroSchemaType.Long => (AvroValue)(1_790_000_000_000L + i),
                    AvroSharp.Schemas.AvroSchemaType.Float => (AvroValue)(i * 0.5f),
                    AvroSharp.Schemas.AvroSchemaType.Double => (AvroValue)(i * 1.25),
                    AvroSharp.Schemas.AvroSchemaType.Boolean => (AvroValue)(i % 3 == 0),
                    _ => (AvroValue)("value " + i),
                },
            };
        }

        _record = record;
        _encoded = _writer.WriteToArray(_record);
        _generated = bench.generated.Wide.FromAvroBytes(_encoded);

        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(bench.generated.Wide.SchemaJson);
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

    [Benchmark]
    [BenchmarkCategory("Write")]
    public long AvroSharp_Generated_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        bench.generated.Wide.Write(ref writer, _generated);
        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public object ApacheAvro_Read() =>
        _apacheReader.Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(_encoded, writable: false)));

    [Benchmark]
    [BenchmarkCategory("Read")]
    public AvroValue AvroSharp_Read() => _reader.Read(_encoded);

    [Benchmark]
    [BenchmarkCategory("Read")]
    public bench.generated.Wide AvroSharp_Generated_Read()
    {
        var reader = new AvroReader(_encoded);
        return bench.generated.Wide.Read(ref reader);
    }
}
