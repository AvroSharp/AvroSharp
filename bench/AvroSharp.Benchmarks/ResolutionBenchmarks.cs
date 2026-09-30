using System;
using System.IO;
using System.Linq;
using AvroSharp.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using EnumSchema = AvroSharp.Schemas.EnumSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Schema evolution (M3's "E" benchmark): reading one record written with version 1 of a schema as version 2.
/// Version 2 drops a field (skipped), promotes int to long and float to double, adds
/// fields with defaults, and reorders enum symbols. Apache.Avro's resolving generic reader is the baseline.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ResolutionBenchmarks
{
    private const string Version1 = """
        {"type":"record","name":"Customer","namespace":"bench.evolved","fields":[
          {"name":"id","type":"int"},
          {"name":"name","type":"string"},
          {"name":"notes","type":{"type":"array","items":"string"}},
          {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["GOLD","SILVER","BASIC"]}},
          {"name":"balance","type":"float"},
          {"name":"visits","type":{"type":"array","items":"int"}}
        ]}
        """;

    // Version 2 with fields the data lacks, whose defaults are values the reader creates per record (#135).
    private const string WithDefaults = """
        {"type":"record","name":"Customer","namespace":"bench.evolved","fields":[
          {"name":"id","type":"long"},
          {"name":"name","type":"string"},
          {"name":"address","type":{"type":"record","name":"Address","fields":[{"name":"city","type":"string"},{"name":"zip","type":["null","string"]}]},"default":{"city":"unknown","zip":null}},
          {"name":"labels","type":{"type":"array","items":"string"},"default":["new","unverified"]},
          {"name":"settings","type":{"type":"map","values":"long"},"default":{"limit":100}},
          {"name":"token","type":"bytes","default":"\u0000\u0001"},
          {"name":"note","type":["null","string"],"default":null}
        ]}
        """;

    private GenericDatumReader _reader = null!;
    private GenericDatumReader _defaultsReader = null!;
    private AvroSchema _writerSchema = null!;
    private Avro.Generic.GenericDatumReader<ApacheGenericRecord> _apacheReader = null!;
    private byte[] _encoded = [];

    [GlobalSetup]
    public void Setup()
    {
        var writer = (RecordSchema)AvroSchema.Parse(Version1);
        var reader = bench.evolved.Customer.Schema;
        _writerSchema = writer;
        _reader = GenericDatumReader.Create(writer, reader);
        _defaultsReader = GenericDatumReader.Create(writer, AvroSchema.Parse(WithDefaults));
        _encoded = GenericDatumWriter.Create(writer).WriteToArray(new GenericRecord(writer)
        {
            ["id"] = 123_456,
            ["name"] = "Customer 42",
            ["notes"] = AvroValue.FromArray(Enumerable.Range(0, 20).Select(i => (AvroValue)$"note {i}").ToList()),
            ["tier"] = AvroValue.FromEnum((EnumSchema)writer.GetField("tier").Schema, "SILVER"),
            ["balance"] = 1234.5f,
            ["visits"] = AvroValue.FromArray(Enumerable.Range(0, 32).Select(i => (AvroValue)(i * 17)).ToList()),
        });

        var apacheWriter = Avro.Schema.Parse(Version1);
        var apacheReader = Avro.Schema.Parse(bench.evolved.Customer.SchemaJson);
        _apacheReader = new Avro.Generic.GenericDatumReader<ApacheGenericRecord>(apacheWriter, apacheReader);

        // Every path must resolve to the same value before its time means anything.
        var ours = GenericDatumWriter.Create(reader).WriteToArray(_reader.Read(_encoded));
        var generated = bench.evolved.Customer.FromAvroBytes(_encoded, _writerSchema).ToAvroBytes();
        using var apacheOutput = new MemoryStream();
        new Avro.Generic.GenericDatumWriter<ApacheGenericRecord>(apacheReader).Write(ApacheAvro_Read(), new Avro.IO.BinaryEncoder(apacheOutput));
        if (!ours.AsSpan().SequenceEqual(apacheOutput.ToArray()) || !ours.AsSpan().SequenceEqual(generated))
        {
            throw new InvalidOperationException("The resolving readers disagree on the resolved value.");
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public ApacheGenericRecord ApacheAvro_Read() =>
        _apacheReader.Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(_encoded, writable: false)));

    [Benchmark]
    [BenchmarkCategory("Read")]
    public AvroValue AvroSharp_Read() => _reader.Read(_encoded);

    // Five fields taken from their defaults: a record, an array, a map, bytes and a null union.
    [Benchmark]
    [BenchmarkCategory("ReadDefaults")]
    public AvroValue AvroSharp_Read_Defaults() => _defaultsReader.Read(_encoded);

    // The generated type (Schemas/customer-v2.avsc) reading version 1 data: resolved generically, then read.
    [Benchmark]
    [BenchmarkCategory("Read")]
    public bench.evolved.Customer AvroSharp_Generated_Read() => bench.evolved.Customer.FromAvroBytes(_encoded, _writerSchema);
}
