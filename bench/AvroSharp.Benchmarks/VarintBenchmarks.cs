using System;
using System.Buffers;
using System.IO;
using System.Linq;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Focused varint benchmark: 1,000 longs whose zig-zag encoding is exactly <see cref="Bytes"/> bytes long,
/// encoded and decoded by AvroSharp and by Apache.Avro. Isolates each varint path (one byte, one word,
/// word plus one or two bytes).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class VarintBenchmarks
{
    private const int Count = 1_000;

    private long[] _values = [];
    private byte[] _encoded = [];
    private ArrayBufferWriter<byte> _output = new();
    private MemoryStream _stream = new();

    /// <summary>The encoded length of every value, in bytes.</summary>
    [Params(1, 2, 5, 8, 9, 10)]
    public int Bytes { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        var low = Bytes == 1 ? 0UL : 1UL << (7 * (Bytes - 1));
        var high = Bytes == 10 ? ulong.MaxValue : (1UL << (7 * Bytes)) - 1;
        _values = Enumerable.Range(0, Count).Select(_ =>
        {
            var zigZag = low + (ulong)(random.NextDouble() * (high - low));
            return (long)(zigZag >> 1) ^ -(long)(zigZag & 1);
        }).ToArray();

        _output = new ArrayBufferWriter<byte>(16 * 1024);
        _stream = new MemoryStream(16 * 1024);
        AvroSharp_Encode();
        _encoded = _output.WrittenSpan.ToArray();
        if (_encoded.Length != Count * Bytes)
        {
            throw new InvalidOperationException($"Expected {Count * Bytes} encoded bytes, got {_encoded.Length}.");
        }
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
        foreach (var value in _values)
        {
            writer.WriteLong(value);
        }

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
        for (var i = 0; i < Count; i++)
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
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum += reader.ReadLong();
        }

        return checksum;
    }
}
