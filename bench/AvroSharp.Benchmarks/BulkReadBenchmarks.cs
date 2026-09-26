using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Reading 64K array items: AvroSharp's bulk ReadLongs (vector check for runs of one-byte values) against
/// Apache.Avro's per-item reads, and against AvroSharp's own one-at-a-time loop. The SIMD rules require the bulk
/// path to beat the scalar loop as well; "AvroSharpScalar" is reported but not gated.
/// </summary>
[MemoryDiagnoser]
public class BulkReadBenchmarks
{
    private const int Count = BenchmarkData.ValueCount;

    private byte[] _encoded = [];
    private long[] _destination = [];

    /// <summary>
    /// SmallValues: every item one byte. Mixed: each item independently one byte (90%) or a timestamp (10%).
    /// Timestamps: every item 7-8 bytes.
    /// </summary>
    [Params("SmallValues", "Mixed", "Timestamps")]
    public string Data { get; set; } = "SmallValues";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(3);
        var values = Enumerable.Range(0, Count).Select(_ => Data switch
        {
            "SmallValues" => random.Next(-64, 64),
            "Mixed" => random.Next(10) == 0 ? 1_790_000_000_000_000L + random.Next() : random.Next(-64, 64),
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
        ReadBulk(ref reader, _destination);
        return _destination[Count - 1];
    }

    [Benchmark]
    public long AvroSharpScalar_ReadLongLoop()
    {
        var reader = new AvroReader(_encoded);
        ReadLoop(ref reader, _destination);
        return _destination[Count - 1];
    }

    // Through a ref parameter, as the generic reader and generated code use the reader.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadBulk(ref AvroReader reader, long[] destination) => reader.ReadLongs(destination);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadLoop(ref AvroReader reader, long[] destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = reader.ReadLong();
        }
    }
}
