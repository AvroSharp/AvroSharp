using System;
using System.Buffers;
using System.IO;
using System.Linq;
using AvroSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Encoding and decoding 1,000 values with AvroSharp's AvroWriter/AvroReader versus Apache.Avro's
/// BinaryEncoder/BinaryDecoder. Both write to a reused buffer and read from the same bytes.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public sealed class BinaryEncodingBenchmarks : IDisposable
{
    private const int Count = 1_000;

    private long[] _longs = [];
    private string[] _strings = [];
    private double[] _doubles = [];
    private byte[][] _bytes = [];
    private byte[] _encoded = [];
    private ArrayBufferWriter<byte> _output = new();
    private MemoryStream _stream = new();

    /// <summary>Longs of mixed magnitudes; ASCII and non-ASCII strings; or a mixture of all value kinds.</summary>
    [Params("Longs", "Strings", "Mixed")]
    public string Workload { get; set; } = "Mixed";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _longs = Enumerable.Range(0, Count).Select(i => (i % 4) switch
        {
            0 => random.Next(-64, 64),
            1 => random.Next(),
            2 => random.NextInt64(),
            _ => -random.NextInt64(),
        }).ToArray();
        _strings = Enumerable.Range(0, Count).Select(i => i % 5 == 0
            ? $"Grüße, 日本 {i}"
            : new string((char)('a' + (i % 26)), 8 + (i % 40))).ToArray();
        _doubles = Enumerable.Range(0, Count).Select(_ => (random.NextDouble() * 2e6) - 1e6).ToArray();
        _bytes = Enumerable.Range(0, Count).Select(_ =>
        {
            var b = new byte[16];
            random.NextBytes(b);
            return b;
        }).ToArray();

        _output = new ArrayBufferWriter<byte>(64 * 1024);
        _stream = new MemoryStream(64 * 1024);
        AvroSharp_Encode();
        _encoded = _output.WrittenSpan.ToArray();
    }

    public void Dispose() => _stream.Dispose();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Encode")]
    public long ApacheAvro_Encode()
    {
        _stream.Position = 0;
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        switch (Workload)
        {
            case "Longs":
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
            case "Longs":
                foreach (var value in _longs)
                {
                    writer.WriteLong(value);
                }

                break;
            case "Strings":
                foreach (var value in _strings)
                {
                    writer.WriteString(value);
                }

                break;
            default:
                for (var i = 0; i < Count; i++)
                {
                    writer.WriteLong(_longs[i]);
                    writer.WriteString(_strings[i]);
                    writer.WriteDouble(_doubles[i]);
                    writer.WriteBytes(_bytes[i]);
                }

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
            case "Longs":
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
        long checksum = 0;
        switch (Workload)
        {
            case "Longs":
                for (var i = 0; i < Count; i++)
                {
                    checksum += reader.ReadLong();
                }

                break;
            case "Strings":
                for (var i = 0; i < Count; i++)
                {
                    checksum += reader.ReadString().Length;
                }

                break;
            default:
                for (var i = 0; i < Count; i++)
                {
                    checksum += reader.ReadLong();
                    checksum += reader.ReadString().Length;
                    checksum += (long)reader.ReadDouble();

                    // Apache.Avro's API can only return a new array; AvroSharp returns a slice of the input.
                    checksum += reader.ReadBytesSpan().Length;
                }

                break;
        }

        return checksum;
    }
}
