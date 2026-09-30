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
/// on a 12-byte fixed, <c>uuid</c> on a string and on a 16-byte fixed, and <c>timestamp-micros</c>. Each logical type and
/// operation is a group, gated against Apache.Avro's logical types (<c>Avro.Util</c>), converting to or from the base value
/// and coding it (#157). Apache.Avro has no <c>uuid</c> on fixed, so that group is AvroSharp only.
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
    private readonly byte[] _fixedBuffer = new byte[FixedSize];

    private Avro.LogicalSchema _apacheDecimal = null!;
    private Avro.LogicalSchema _apacheDecimalFixed = null!;
    private Avro.FixedSchema _apacheFixed = null!;
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

        _decimalBytes = Encode(AvroSharp_DecimalBytes_Write);
        _decimalFixed = Encode(AvroSharp_DecimalFixed_Write);
        _uuidStrings = Encode(AvroSharp_UuidString_Write);
        _uuidFixed = Encode(AvroSharp_UuidFixed_Write);
        _timestampLongs = Encode(AvroSharp_Timestamp_Write);

        _apacheDecimal = (Avro.LogicalSchema)Avro.Schema.Parse($$"""{"type":"bytes","logicalType":"decimal","precision":{{Precision}},"scale":{{Scale}}}""");
        _apacheDecimalFixed = (Avro.LogicalSchema)Avro.Schema.Parse($$"""{"type":"fixed","name":"Amount","size":{{FixedSize}},"logicalType":"decimal","precision":{{Precision}},"scale":{{Scale}}}""");
        _apacheFixed = (Avro.FixedSchema)_apacheDecimalFixed.BaseSchema;
        _apacheUuid = (Avro.LogicalSchema)Avro.Schema.Parse("""{"type":"string","logicalType":"uuid"}""");
        _apacheTimestamp = (Avro.LogicalSchema)Avro.Schema.Parse("""{"type":"long","logicalType":"timestamp-micros"}""");

        // Each read must give back what was written, and Apache's encodings must be the same bytes, before a time means anything.
        var sum = _decimals.Sum();
        var ticks = _timestamps.Aggregate(0L, (sum, t) => unchecked(sum + t.UtcTicks));   // wraps, as the reads do
        if (AvroSharp_DecimalBytes_Read() != sum || AvroSharp_DecimalFixed_Read() != sum || AvroSharp_UuidString_Read() != Checksum(_uuids)
            || AvroSharp_UuidFixed_Read() != Checksum(_uuids) || AvroSharp_Timestamp_Read() != ticks)
        {
            throw new InvalidOperationException("A logical value did not read back as written.");
        }

        if (!SameBytes(ApacheAvro_DecimalBytes_Write, _decimalBytes) || !SameBytes(ApacheAvro_DecimalFixed_Write, _decimalFixed)
            || !SameBytes(ApacheAvro_UuidString_Write, _uuidStrings) || !SameBytes(ApacheAvro_Timestamp_Write, _timestampLongs))
        {
            throw new InvalidOperationException("Apache.Avro encodes the logical values differently.");
        }

        if (ApacheAvro_DecimalBytes_Read() != sum || ApacheAvro_DecimalFixed_Read() != sum || ApacheAvro_UuidString_Read() != Checksum(_uuids)
            || ApacheAvro_Timestamp_Read() != ticks)
        {
            throw new InvalidOperationException("Apache.Avro reads the logical values differently.");
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DecimalBytes", "Write")]
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
    [BenchmarkCategory("DecimalBytes", "Write")]
    public long AvroSharp_DecimalBytes_Write()
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

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DecimalBytes", "Read")]
    public decimal ApacheAvro_DecimalBytes_Read()
    {
        var decoder = new Avro.IO.BinaryDecoder(new MemoryStream(_decimalBytes, writable: false));
        var sum = 0m;
        for (var i = 0; i < Count; i++)
        {
            sum += ((Avro.AvroDecimal)_apacheDecimalType.ConvertToLogicalValue(decoder.ReadBytes(), _apacheDecimal)).ToType<decimal>();
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalBytes", "Read")]
    public decimal AvroSharp_DecimalBytes_Read()
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
    [BenchmarkCategory("DecimalFixed", "Write")]
    public long ApacheAvro_DecimalFixed_Write()
    {
        _stream.SetLength(0);
        var encoder = new Avro.IO.BinaryEncoder(_stream);
        foreach (var value in _decimals)
        {
            encoder.WriteFixed(((Avro.Generic.GenericFixed)_apacheDecimalType.ConvertToBaseValue(new Avro.AvroDecimal(value), _apacheDecimalFixed)).Value);
        }

        encoder.Flush();
        return _stream.Length;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalFixed", "Write")]
    public long AvroSharp_DecimalFixed_Write()
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

    // Apache's generic reader wraps each fixed value in a GenericFixed before converting it, as done here.
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DecimalFixed", "Read")]
    public decimal ApacheAvro_DecimalFixed_Read()
    {
        var decoder = new Avro.IO.BinaryDecoder(new MemoryStream(_decimalFixed, writable: false));
        var sum = 0m;
        for (var i = 0; i < Count; i++)
        {
            decoder.ReadFixed(_fixedBuffer);
            var value = new Avro.Generic.GenericFixed(_apacheFixed, _fixedBuffer);
            sum += ((Avro.AvroDecimal)_apacheDecimalType.ConvertToLogicalValue(value, _apacheDecimalFixed)).ToType<decimal>();
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("DecimalFixed", "Read")]
    public decimal AvroSharp_DecimalFixed_Read()
    {
        var reader = new AvroReader(_decimalFixed);
        var sum = 0m;
        for (var i = 0; i < Count; i++)
        {
            sum += AvroLogicalValues.ReadDecimalFixed(ref reader, Scale, FixedSize);
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UuidString", "Write")]
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
    [BenchmarkCategory("UuidString", "Write")]
    public long AvroSharp_UuidString_Write()
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

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UuidString", "Read")]
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
    [BenchmarkCategory("UuidString", "Read")]
    public long AvroSharp_UuidString_Read()
    {
        var reader = new AvroReader(_uuidStrings);
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum ^= AvroLogicalValues.ReadUuidString(ref reader).GetHashCode();
        }

        return checksum;
    }

    // Apache.Avro 1.12 rejects uuid on fixed ("'uuid' can only be used with an underlying string type").
    [Benchmark]
    [BenchmarkCategory("UuidFixed", "Write", Gate.Ungated)]
    public long AvroSharp_UuidFixed_Write()
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
    [BenchmarkCategory("UuidFixed", "Read", Gate.Ungated)]
    public long AvroSharp_UuidFixed_Read()
    {
        var reader = new AvroReader(_uuidFixed);
        long checksum = 0;
        for (var i = 0; i < Count; i++)
        {
            checksum ^= AvroLogicalValues.ReadUuidFixed(ref reader).GetHashCode();
        }

        return checksum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Timestamp", "Write")]
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

    [Benchmark]
    [BenchmarkCategory("Timestamp", "Write")]
    public long AvroSharp_Timestamp_Write()
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

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Timestamp", "Read")]
    public long ApacheAvro_Timestamp_Read()
    {
        var decoder = new Avro.IO.BinaryDecoder(new MemoryStream(_timestampLongs, writable: false));
        long sum = 0;
        for (var i = 0; i < Count; i++)
        {
            sum += ((DateTime)_apacheTimestampType.ConvertToLogicalValue(decoder.ReadLong(), _apacheTimestamp)).Ticks;
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Timestamp", "Read")]
    public long AvroSharp_Timestamp_Read()
    {
        var reader = new AvroReader(_timestampLongs);
        long sum = 0;
        for (var i = 0; i < Count; i++)
        {
            sum += AvroLogicalValues.TimestampFromMicroseconds(reader.ReadLong()).UtcTicks;
        }

        return sum;
    }

    private static long Checksum(Guid[] uuids) => uuids.Aggregate(0L, (sum, uuid) => sum ^ uuid.GetHashCode());

    private byte[] Encode(Func<long> write)
    {
        write();
        return _output.WrittenSpan.ToArray();
    }

    private bool SameBytes(Func<long> apacheWrite, byte[] expected)
    {
        apacheWrite();
        return _stream.ToArray().AsSpan().SequenceEqual(expected);
    }
}
