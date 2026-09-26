using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Encoding and decoding 64K random values with AvroSharp's AvroWriter/AvroReader versus Apache.Avro's
/// BinaryEncoder/BinaryDecoder. Both write to a reused buffer and read from the same bytes. Every value's kind and
/// size is drawn at random, so the data has no pattern for the branch predictor to learn.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BinaryEncodingBenchmarks
{
    private const int Count = BenchmarkData.ValueCount;

    private long[] _longs = [];
    private string[] _strings = [];
    private double[] _doubles = [];
    private byte[][] _bytes = [];
    private byte[] _encoded = [];
    private ArrayBufferWriter<byte> _output = new();
    private MemoryStream _stream = new();

    /// <summary>
    /// Longs: full-range random values (mostly 5-10 bytes each). RealisticLongs: what records usually hold, mostly
    /// small values plus timestamps. Strings: ASCII and non-ASCII. Mixed: longs, strings, doubles and bytes.
    /// </summary>
    [Params("Longs", "RealisticLongs", "Strings", "Mixed")]
    public string Workload { get; set; } = "Mixed";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _longs = string.Equals(Workload, "RealisticLongs", StringComparison.Ordinal)
            // 60% small values (counts, ids, enums, lengths), 25% timestamp-micros around 2026, 15% medium values.
            ? Enumerable.Range(0, Count).Select(_ => random.Next(20) switch
            {
                < 12 => random.Next(-1_000, 1_000),
                < 17 => 1_790_000_000_000_000L + random.NextInt64(0, 31_536_000_000_000L),
                _ => random.Next(0, 10_000_000),
            }).ToArray()
            : Enumerable.Range(0, Count).Select(_ => random.Next(4) switch
            {
                0 => random.Next(-64, 64),
                1 => random.Next(),
                2 => random.NextInt64(),
                _ => -random.NextInt64(),
            }).ToArray();

        _strings = Enumerable.Range(0, Count).Select(_ => random.Next(5) == 0
            ? $"Grüße, 日本 {random.Next()}"
            : new string((char)('a' + random.Next(26)), 8 + random.Next(40))).ToArray();
        _doubles = Enumerable.Range(0, Count).Select(_ => (random.NextDouble() * 2e6) - 1e6).ToArray();
        _bytes = Enumerable.Range(0, Count).Select(_ =>
        {
            var b = new byte[16];
            random.NextBytes(b);
            return b;
        }).ToArray();

        _output = new ArrayBufferWriter<byte>(Count * 96);
        _stream = new MemoryStream(Count * 96);
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
        switch (Workload)
        {
            case "Longs" or "RealisticLongs":
                foreach (var value in _longs)
                {
                    encoder.WriteLong(value);
                }

                break;
            case "Strings":
                foreach (var value in _strings)
                {
                    encoder.WriteString(value);
                }

                break;
            default:
                for (var i = 0; i < Count; i++)
                {
                    encoder.WriteLong(_longs[i]);
                    encoder.WriteString(_strings[i]);
                    encoder.WriteDouble(_doubles[i]);
                    encoder.WriteBytes(_bytes[i]);
                }

                break;
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
        switch (Workload)
        {
            case "Longs" or "RealisticLongs":
                EncodeLongs(ref writer, _longs);
                break;
            case "Strings":
                EncodeStrings(ref writer, _strings);
                break;
            default:
                EncodeMixed(ref writer, _longs, _strings, _doubles, _bytes);
                break;
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
        switch (Workload)
        {
            case "Longs" or "RealisticLongs":
                for (var i = 0; i < Count; i++)
                {
                    checksum += decoder.ReadLong();
                }

                break;
            case "Strings":
                for (var i = 0; i < Count; i++)
                {
                    checksum += decoder.ReadString().Length;
                }

                break;
            default:
                for (var i = 0; i < Count; i++)
                {
                    checksum += decoder.ReadLong();
                    checksum += decoder.ReadString().Length;
                    checksum += (long)decoder.ReadDouble();
                    checksum += decoder.ReadBytes().Length;
                }

                break;
        }

        return checksum;
    }

    [Benchmark]
    [BenchmarkCategory("Decode")]
    public long AvroSharp_Decode()
    {
        var reader = new AvroReader(_encoded);
        return Workload switch
        {
            "Longs" or "RealisticLongs" => DecodeLongs(ref reader, Count),
            "Strings" => DecodeStrings(ref reader, Count),
            _ => DecodeMixed(ref reader, Count),
        };
    }

    // The helpers take the reader or writer by ref, as record code does: a local could be kept in registers, which
    // hides the cost of storing and reloading its position.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeLongs(ref AvroWriter writer, long[] values)
    {
        foreach (var value in values)
        {
            writer.WriteLong(value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeStrings(ref AvroWriter writer, string[] values)
    {
        foreach (var value in values)
        {
            writer.WriteString(value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeMixed(ref AvroWriter writer, long[] longs, string[] strings, double[] doubles, byte[][] bytes)
    {
        for (var i = 0; i < longs.Length; i++)
        {
            writer.WriteLong(longs[i]);
            writer.WriteString(strings[i]);
            writer.WriteDouble(doubles[i]);
            writer.WriteBytes(bytes[i]);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DecodeLongs(ref AvroReader reader, int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            checksum += reader.ReadLong();
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DecodeStrings(ref AvroReader reader, int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            checksum += reader.ReadString().Length;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DecodeMixed(ref AvroReader reader, int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            checksum += reader.ReadLong();
            checksum += reader.ReadString().Length;
            checksum += (long)reader.ReadDouble();

            // Apache.Avro's API can only return a new array; AvroSharp returns a slice of the input.
            checksum += reader.ReadBytesSpan().Length;
        }

        return checksum;
    }
}
