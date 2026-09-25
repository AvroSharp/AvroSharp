using System;
using System.Buffers;
using System.IO;
using System.Linq;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Reading 1,000 array items: AvroSharp's bulk ReadLongs (vector check for runs of one-byte values) against
/// Apache.Avro's per-item reads, and against AvroSharp's own one-at-a-time loop. The SIMD rules require the bulk
/// path to beat the scalar loop as well; "AvroSharpScalar" is reported but not gated.
/// </summary>
[MemoryDiagnoser]
public class BulkReadBenchmarks
{
    private const int Count = 1_000;

    private byte[] _encoded = [];
    private long[] _destination = [];

    /// <summary>SmallValues: every item one byte. Mixed: 90% one byte, 10% timestamps. Timestamps: every item 7-8 bytes.</summary>
    [Params("SmallValues", "Mixed", "Timestamps")]
    public string Data { get; set; } = "SmallValues";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(3);
        var values = Enumerable.Range(0, Count).Select(i => Data switch
        {
            "SmallValues" => random.Next(-64, 64),
            "Mixed" => i % 10 == 0 ? 1_790_000_000_000_000L + random.Next() : random.Next(-64, 64),
            _ => 1_790_000_000_000_000L + random.NextInt64(0, 31_536_000_000_000L),
        }).ToArray();

        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        foreach (var value in values)
        {
            writer.WriteLong(value);
        }

        writer.Flush();
        _encoded = output.WrittenSpan.ToArray();
        _destination = new long[Count];
    }

    [Benchmark(Baseline = true)]
    public long ApacheAvro_ReadItems()
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
    public long AvroSharp_ReadLongs()
    {
        var reader = new AvroReader(_encoded);
        reader.ReadLongs(_destination);
        return _destination[Count - 1];
    }

    [Benchmark]
    public long AvroSharpScalar_ReadLongLoop()
    {
        var reader = new AvroReader(_encoded);
        for (var i = 0; i < Count; i++)
        {
            _destination[i] = reader.ReadLong();
        }

        return _destination[Count - 1];
    }
}
