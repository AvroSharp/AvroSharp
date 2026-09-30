using System;
using System.Buffers;
using System.IO;
using System.Linq;
using AvroSharp.IO;
using AvroSharp.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Converting and coding 1,024 logical values (#135), as generated code does per field: <c>decimal(18,4)</c> on bytes and
/// on a 12-byte fixed, <c>uuid</c> on a string and on a 16-byte fixed, and <c>timestamp-micros</c>. Apache.Avro's
/// logical types (<c>Avro.Util</c>), converting to the base value and coding it, are the baseline where they exist.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class LogicalTypeBenchmarks
{
    private const int Count = 1024;
    private const int Scale = 4;
    private const int Precision = 18;
    private const int FixedSize = 12;

    private decimal[] _decimals = [];
    private Guid[] _uuids = [];
    private DateTimeOffset[] _timestamps = [];
    private byte[] _decimalBytes = [];
    private byte[] _decimalFixed = [];
    private byte[] _uuidStrings = [];
    private byte[] _uuidFixed = [];
    private byte[] _timestampLongs = [];
    private ArrayBufferWriter<byte> _output = new();
    private readonly MemoryStream _stream = new();

    private Avro.LogicalSchema _apacheDecimal = null!;
    private Avro.LogicalSchema _apacheUuid = null!;
    private Avro.LogicalSchema _apacheTimestamp = null!;
    private readonly Avro.Util.Decimal _apacheDecimalType = new();
    private readonly Avro.Util.Uuid _apacheUuidType = new();
    private readonly Avro.Util.TimestampMicrosecond _apacheTimestampType = new();

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);

        // Up to 14 integer digits and 4 fractional ones, as money amounts are; some negative, some with fewer digits.
        _decimals = Enumerable.Range(0, Count).Select(_ =>
        {
            var unscaled = random.NextInt64(0, 999_999_999_999_999_999L) / (long)Math.Pow(10, random.Next(12));
            return new decimal(unchecked((int)unscaled), (int)(unscaled >> 32), 0, random.Next(4) == 0, Scale);
        }).ToArray();
        _uuids = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToArray();
        _timestamps = Enumerable.Range(0, Count).Select(_ => DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000L + random.NextInt64(0, 31_536_000_000L))).ToArray();
        _output = new ArrayBufferWriter<byte>(Count * 40);

        _decimalBytes = Encode(DecimalBytes_Write);
        _decimalFixed = Encode(DecimalFixed_Write);
        _uuidStrings = Encode(UuidString_Write);
        _uuidFixed = Encode(UuidFixed_Write);
        _timestampLongs = Encode(Timestamp_Write);

        _apacheDecimal = (Avro.LogicalSchema)Avro.Schema.Parse($$"""{"type":"bytes","logicalType":"decimal","precision":{{Precision}},"scale":{{Scale}}}""");
        _apacheUuid = (Avro.LogicalSchema)Avro.Schema.Parse("""{"type":"string","logicalType":"uuid"}""");
        _apacheTimestamp = (Avro.LogicalSchema)Avro.Schema.Parse("""{"type":"long","logicalType":"timestamp-micros"}""");

        // Each read must give back what was written, and Apache's encodings must be the same bytes, before a time means anything.
        if (DecimalBytes_Read() != _decimals.Sum() || DecimalFixed_Read() != _decimals.Sum() || UuidString_Read() != Checksum(_uuids) || UuidFixed_Read() != Checksum(_uuids))
        {
            throw new InvalidOperationException("A logical value did not read back as written.");
        }

        ApacheAvro_DecimalBytes_Write();
        var apacheDecimal = _stream.ToArray();
        ApacheAvro_UuidString_Write();
        var apacheUuid = _stream.ToArray();
        if (!apacheDecimal.AsSpan().SequenceEqual(_decimalBytes) || !apacheUuid.AsSpan().SequenceEqual(_uuidStrings))
        {
            throw new InvalidOperationException("Apache.Avro encodes the logical values differently.");
        }
    }

    [Benchmark]
    [BenchmarkCategory("DecimalBytes")]
    public long DecimalBytes_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        foreach (var value in _decimals)
        {
            AvroLogicalValues.WriteDecimalBytes(ref writer, value, Scale, Precision);
        }

        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalBytes")]
    public decimal DecimalBytes_Read()
    {
        var reader = new AvroReader(_decimalBytes);
        var sum = 0m;
        for (var i = 0; i < Count; i++)
        {
            sum += AvroLogicalValues.ReadDecimalBytes(ref reader, Scale);
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DecimalBytes")]
    public long ApacheAvro_DecimalBytes_Write()
    {
        _stream.SetLength(0);
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        foreach (var value in _decimals)
        {
            encoder.WriteBytes((byte[])_apacheDecimalType.ConvertToBaseValue(new Avro.AvroDecimal(value), _apacheDecimal));
        }

        encoder.Flush();
        return _stream.Length;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalFixed")]
    public long DecimalFixed_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        foreach (var value in _decimals)
        {
            AvroLogicalValues.WriteDecimalFixed(ref writer, value, Scale, Precision, FixedSize);
        }

        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalFixed")]
    public decimal DecimalFixed_Read()
    {
        var reader = new AvroReader(_decimalFixed);
        var sum = 0m;
        for (var i = 0; i < Count; i++)
        {
            sum += AvroLogicalValues.ReadDecimalFixed(ref reader, Scale, FixedSize);
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("UuidString")]
    public long UuidString_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        foreach (var value in _uuids)
        {
            AvroLogicalValues.WriteUuidString(ref writer, value);
        }

        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("UuidString")]
    public long UuidString_Read()
    {
        var reader = new AvroReader(_uuidStrings);
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum ^= AvroLogicalValues.ReadUuidString(ref reader).GetHashCode();
        }

        return checksum;
    }

    [Benchmark]
    [BenchmarkCategory("UuidString")]
    public long ApacheAvro_UuidString_Write()
    {
        _stream.SetLength(0);
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        foreach (var value in _uuids)
        {
            encoder.WriteString((string)_apacheUuidType.ConvertToBaseValue(value, _apacheUuid));
        }

        encoder.Flush();
        return _stream.Length;
    }

    [Benchmark]
    [BenchmarkCategory("UuidString")]
    public long ApacheAvro_UuidString_Read()
    {
        var decoder = new Avro.IO.BinaryDecoder(new MemoryStream(_uuidStrings, writable: false));
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum ^= ((Guid)_apacheUuidType.ConvertToLogicalValue(decoder.ReadString(), _apacheUuid)).GetHashCode();
        }

        return checksum;
    }

    [Benchmark]
    [BenchmarkCategory("UuidFixed")]
    public long UuidFixed_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        foreach (var value in _uuids)
        {
            AvroLogicalValues.WriteUuidFixed(ref writer, value);
        }

        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("UuidFixed")]
    public long UuidFixed_Read()
    {
        var reader = new AvroReader(_uuidFixed);
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum ^= AvroLogicalValues.ReadUuidFixed(ref reader).GetHashCode();
        }

        return checksum;
    }

    [Benchmark]
    [BenchmarkCategory("Timestamp")]
    public long Timestamp_Write()
    {
        _output.ResetWrittenCount();
        var writer = new AvroWriter(_output);
        foreach (var value in _timestamps)
        {
            writer.WriteLong(AvroLogicalValues.MicrosecondsFromTimestamp(value));
        }

        writer.Flush();
        return _output.WrittenCount;
    }

    [Benchmark]
    [BenchmarkCategory("Timestamp")]
    public long Timestamp_Read()
    {
        var reader = new AvroReader(_timestampLongs);
        long sum = 0;
        for (var i = 0; i < Count; i++)
        {
            sum += AvroLogicalValues.TimestampFromMicroseconds(reader.ReadLong()).UtcTicks;
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Timestamp")]
    public long ApacheAvro_Timestamp_Write()
    {
        _stream.SetLength(0);
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        foreach (var value in _timestamps)
        {
            encoder.WriteLong((long)_apacheTimestampType.ConvertToBaseValue(value.UtcDateTime, _apacheTimestamp));
        }

        encoder.Flush();
        return _stream.Length;
    }

    private static long Checksum(Guid[] uuids) => uuids.Aggregate(0L, (sum, uuid) => sum ^ uuid.GetHashCode());

    private byte[] Encode(Func<long> write)
    {
        write();
        return _output.WrittenSpan.ToArray();
    }
}
