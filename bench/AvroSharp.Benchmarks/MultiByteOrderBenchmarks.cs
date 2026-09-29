using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AvroSharp.Benchmarks;

/// <summary>
/// TEMPORARY (#102): three orders of the length tests in the out-of-line varint writer, on the VarintBenchmarks data,
/// in one run, for single and bulk writes. Order sets AVROSHARP_MULTIBYTE_ORDER before the first write of the
/// benchmark process (see MultiByteOrderVariant). Meaningful only without fast PDEP, where every value above 2 bytes
/// takes that writer. Remove before merging.
/// </summary>
[MemoryDiagnoser]
public class MultiByteOrderBenchmarks
{
    private long[] _values = [];
    private ArrayBufferWriter<byte> _output = new();

    [ParamsSource(nameof(Lengths))]
    public string Bytes { get; set; } = "3";

    [Params("0", "1", "2")]
    public string Order { get; set; } = "0";

    public static string[] Lengths()
    {
        var selected = Environment.GetEnvironmentVariable("AVROSHARP_VARINT_BYTES");
        return string.IsNullOrWhiteSpace(selected)
            ? ["3", "4", "5", "8", "10", "Mixed1-10"]
            : selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [GlobalSetup]
    public void Setup()
    {
        Environment.SetEnvironmentVariable("AVROSHARP_MULTIBYTE_ORDER", Order);
        var random = new Random(7);
        _values = Enumerable.Range(0, BenchmarkData.ValueCount).Select(_ => Bytes switch
        {
            "Mixed1-10" => BenchmarkData.LongOfEncodedLength(random, random.Next(1, 11)),
            _ => BenchmarkData.LongOfEncodedLength(random, int.Parse(Bytes, CultureInfo.InvariantCulture)),
        }).ToArray();
        _output = new ArrayBufferWriter<byte>(BenchmarkData.ValueCount * 10);
    }

    [Benchmark]
    public long AvroSharp_Encode()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        EncodeAll(ref writer, _values);
        writer.Flush();
        return writer.BytesWritten;
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

    // As VarintBenchmarks: through a ref parameter, as record code uses the writer.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeAll(ref AvroWriter writer, long[] values)
    {
        foreach (var value in values)
        {
            writer.WriteLong(value);
        }
    }
}
