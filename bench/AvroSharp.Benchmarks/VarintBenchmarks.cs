using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Focused varint benchmark: 64K random longs per operation, encoded and decoded by AvroSharp and by Apache.Avro.
/// A number selects values of exactly that encoded length (1 to 10 bytes); "Mixed1-10" and "Mixed1-2" draw each
/// value's length at random, so value lengths cannot be predicted.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class VarintBenchmarks
{
    private long[] _values = [];
    private byte[] _encoded = [];
    private ArrayBufferWriter<byte> _output = new();
    private MemoryStream _stream = new();

    /// <summary>The encoded length of the values, or a mixed-length workload.</summary>
    [ParamsSource(nameof(Lengths))]
    public string Bytes { get; set; } = "1";

    /// <summary>
    /// The workloads to run: all by default, or a comma-separated list from the AVROSHARP_VARINT_BYTES environment
    /// variable (for example "3,4" or "Mixed1-10") to focus a run.
    /// </summary>
    public static string[] Lengths()
    {
        var selected = Environment.GetEnvironmentVariable("AVROSHARP_VARINT_BYTES");
        return string.IsNullOrWhiteSpace(selected)
            ? ["1", "2", "3", "4", "5", "8", "10", "Mixed1-10", "Mixed1-2"]
            : selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        _values = Enumerable.Range(0, BenchmarkData.ValueCount).Select(_ => Bytes switch
        {
            "Mixed1-10" => BenchmarkData.LongOfEncodedLength(random, random.Next(1, 11)),
            "Mixed1-2" => BenchmarkData.LongOfEncodedLength(random, random.Next(1, 3)),
            _ => BenchmarkData.LongOfEncodedLength(random, int.Parse(Bytes, CultureInfo.InvariantCulture)),
        }).ToArray();

        _output = new ArrayBufferWriter<byte>(BenchmarkData.ValueCount * 10);
        _stream = new MemoryStream(BenchmarkData.ValueCount * 10);
        AvroSharp_Encode();
        _encoded = _output.WrittenSpan.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => _stream.Dispose();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Encode")]
    public long ApacheAvro_Encode()
    {
        _stream.Position = 0;
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        foreach (var value in _values)
        {
            encoder.WriteLong(value);
        }

        encoder.Flush();
        return _stream.Position;
    }

    [Benchmark]
    [BenchmarkCategory("Encode")]
    public long AvroSharp_Encode()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        EncodeAll(ref writer, _values);
        writer.Flush();
        return writer.BytesWritten;
    }

    /// <summary>The same values through <c>WriteLongs</c>, as array items are written.</summary>
    [Benchmark]
    [BenchmarkCategory("Encode")]
    public long AvroSharp_EncodeBulk()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        writer.WriteLongs(_values);
        writer.Flush();
        return writer.BytesWritten;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Decode")]
    public long ApacheAvro_Decode()
    {
        using var stream = new MemoryStream(_encoded, writable: false);
        var decoder = new Avro.IO.BinaryDecoder(stream);
        long checksum = 0;
        for (var i = 0; i < _values.Length; i++)
        {
            checksum += decoder.ReadLong();
        }

        return checksum;
    }

    [Benchmark]
    [BenchmarkCategory("Decode")]
    public long AvroSharp_Decode()
    {
        var reader = new AvroReader(_encoded);
        return DecodeAll(ref reader, _values.Length);
    }

    // Through a ref parameter, as record code uses the reader and writer: a local reader or writer could be kept
    // in registers, which hides the cost of storing and reloading its position.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DecodeAll(ref AvroReader reader, int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            checksum += reader.ReadLong();
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeAll(ref AvroWriter writer, long[] values)
    {
        foreach (var value in values)
        {
            writer.WriteLong(value);
        }
    }
}
