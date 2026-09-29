using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AvroSharp.Benchmarks;

/// <summary>
/// TEMPORARY (#102): the three bulk varint write candidates, on the VarintBenchmarks data, in one run. Variant sets
/// AVROSHARP_BULK_VARIANT before the first bulk write of the benchmark process (see BulkWriteVariant). Only
/// meaningful where PDEP is fast (FastBmi2); elsewhere every variant runs the same code. Remove before merging.
/// </summary>
[MemoryDiagnoser]
public class BulkWriteVariantBenchmarks
{
    private long[] _values = [];
    private ArrayBufferWriter<byte> _output = new();

    [ParamsSource(nameof(Lengths))]
    public string Bytes { get; set; } = "1";

    [Params("0", "1", "2")]
    public string Variant { get; set; } = "0";

    public static string[] Lengths() => VarintBenchmarks.Lengths();

    [GlobalSetup]
    public void Setup()
    {
        Environment.SetEnvironmentVariable("AVROSHARP_BULK_VARIANT", Variant);
        var random = new Random(7);
        _values = Enumerable.Range(0, BenchmarkData.ValueCount).Select(_ => Bytes switch
        {
            "Mixed1-10" => BenchmarkData.LongOfEncodedLength(random, random.Next(1, 11)),
            "Mixed1-2" => BenchmarkData.LongOfEncodedLength(random, random.Next(1, 3)),
            _ => BenchmarkData.LongOfEncodedLength(random, int.Parse(Bytes, CultureInfo.InvariantCulture)),
        }).ToArray();
        _output = new ArrayBufferWriter<byte>(BenchmarkData.ValueCount * 10);
    }

    [Benchmark]
    public long AvroSharp_EncodeBulk()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        writer.WriteLongs(_values);
        writer.Flush();
        return writer.BytesWritten;
    }
}
