using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Object container files (M5): writing and reading a file of 1,000 orders (the record of GenericRecordBenchmarks)
/// in memory, per codec, with the same 64 KiB sync interval. Apache.Avro's DataFileWriter/DataFileReader with its
/// generic datum writer and reader is the baseline.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ContainerBenchmarks
{
    private const int Records = 1_000;
    private const int SyncInterval = 64 * 1024;

    private AvroSchema _schema = null!;
    private Avro.Schema _apacheSchema = null!;
    private AvroValue[] _records = [];
    private ApacheGenericRecord[] _apacheRecords = [];
    private bench.generated.Order[] _generated = [];
    private byte[] _file = [];
    private readonly MemoryStream _output = new();

    // Every codec in the specification. Apache.Avro's codec packages (each at its default level, as ours are) are the
    // baselines; its xz and zstandard ones wrap native libraries, and Zstandard.Net has them for Windows only.
    [Params("null", "deflate", "snappy", "zstandard", "bzip2", "xz")]
    public string Codec { get; set; } = "null";

    private static readonly AvroFileReaderOptions s_readerOptions = new() { Codecs = AvroCodecs.All };

    private AvroCodec OurCodec => Codec switch
    {
        AvroCodecNames.Null => AvroCodec.Null,
        AvroCodecNames.Deflate => AvroCodec.Deflate,
        _ => AvroCodecs.All.Single(c => string.Equals(c.Name, Codec, StringComparison.Ordinal)),
    };

    private Avro.File.Codec ApacheCodec() => Codec switch
    {
        AvroCodecNames.Null => Avro.File.Codec.CreateCodec(Avro.File.Codec.Type.Null),
        AvroCodecNames.Deflate => Avro.File.Codec.CreateCodec(Avro.File.Codec.Type.Deflate),
        AvroCodecNames.Snappy => new Avro.File.Snappy.SnappyCodec(),
        AvroCodecNames.Zstandard => new Avro.File.Zstandard.ZstandardCodec(),
        AvroCodecNames.Bzip2 => new Avro.File.BZip2.BZip2Codec(),
        AvroCodecNames.Xz => new Avro.File.XZ.XZCodec(),
        _ => throw new InvalidOperationException(Codec),
    };

    // Apache's reader finds codecs other than null and deflate through global resolvers.
    private static readonly Lazy<bool> s_apacheCodecs = new(() =>
    {
        Avro.File.Codec.RegisterResolver(name => name switch
        {
            AvroCodecNames.Snappy => new Avro.File.Snappy.SnappyCodec(),
            AvroCodecNames.Zstandard => new Avro.File.Zstandard.ZstandardCodec(),
            AvroCodecNames.Bzip2 => new Avro.File.BZip2.BZip2Codec(),
            AvroCodecNames.Xz => new Avro.File.XZ.XZCodec(),
            _ => null,
        });
        return true;
    });

    [GlobalSetup]
    public void Setup()
    {
        _ = s_apacheCodecs.Value;
        var single = new GenericRecordBenchmarks();
        single.Setup();
        _schema = AvroSchema.Parse(GenericRecordBenchmarks.OrderJson);
        _apacheSchema = Avro.Schema.Parse(GenericRecordBenchmarks.OrderJson);
        var record = single.AvroSharp_Read();
        _records = Enumerable.Repeat(record, Records).ToArray();
        _apacheRecords = Enumerable.Range(0, Records).Select(_ => (ApacheGenericRecord)single.ApacheAvro_Read()).ToArray();
        _generated = Enumerable.Range(0, Records).Select(_ => single.AvroSharp_Generated_Read()).ToArray();

        // Each library must read the other's file back to the same objects.
        _file = WriteTo(AvroSharp_Write);
        var apacheFile = WriteTo(ApacheAvro_Write);
        using var ourReader = AvroFileReader.OpenGeneric(new MemoryStream(apacheFile), options: s_readerOptions);
        var fromApache = ourReader.ReadAll().ToList();
        using var apacheReader = Avro.File.DataFileReader<ApacheGenericRecord>.OpenReader(new MemoryStream(_file), _apacheSchema);
        var apacheCount = 0;
        while (apacheReader.HasNext())
        {
            apacheReader.Next();
            apacheCount++;
        }

        if (fromApache.Count != Records || fromApache.Any(r => !r.Equals(record)) || apacheCount != Records || AvroSharp_Generated_Read() != Records)
        {
            throw new InvalidOperationException("The container readers and writers disagree.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _output.Dispose();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Write")]
    public long ApacheAvro_Write()
    {
        _output.SetLength(0);
        var codec = ApacheCodec();
        using (var writer = Avro.File.DataFileWriter<ApacheGenericRecord>.OpenWriter(new Avro.Generic.GenericDatumWriter<ApacheGenericRecord>(_apacheSchema), _output, codec, leaveOpen: true))
        {
            writer.SetSyncInterval(SyncInterval);
            foreach (var record in _apacheRecords)
            {
                writer.Append(record);
            }
        }

        return _output.Length;
    }

    [Benchmark]
    [BenchmarkCategory("Write")]
    public long AvroSharp_Write()
    {
        _output.SetLength(0);
        using (var writer = AvroFileWriter.CreateGeneric(_output, _schema, Options()))
        {
            foreach (var record in _records)
            {
                writer.Write(record);
            }
        }

        return _output.Length;
    }

    [Benchmark]
    [BenchmarkCategory("Write")]
    public long AvroSharp_Generated_Write()
    {
        _output.SetLength(0);
        using (var writer = AvroFileWriter.Create<bench.generated.Order>(_output, bench.generated.Order.Schema, bench.generated.Order.Write, Options()))
        {
            foreach (var record in _generated)
            {
                writer.Write(record);
            }
        }

        return _output.Length;
    }

    // The asynchronous path: blocks are written with WriteAsync on a MemoryStream (which completes synchronously), so
    // this measures the async machinery's overhead, not I/O overlap.
    [Benchmark]
    [BenchmarkCategory("Write")]
    public async Task<long> AvroSharp_WriteAsync()
    {
        _output.SetLength(0);
        var writer = AvroFileWriter.CreateGeneric(_output, _schema, Options());
        await using (writer.ConfigureAwait(false))
        {
            foreach (var record in _records)
            {
                await writer.WriteAsync(record).ConfigureAwait(false);
            }
        }

        return _output.Length;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public int ApacheAvro_Read()
    {
        using var reader = Avro.File.DataFileReader<ApacheGenericRecord>.OpenReader(new MemoryStream(_file, writable: false), _apacheSchema);
        var count = 0;
        while (reader.HasNext())
        {
            reader.Next();
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Read")]
    public int AvroSharp_Read()
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(_file, writable: false), options: s_readerOptions);
        var count = 0;
        while (reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Read")]
    public int AvroSharp_Generated_Read()
    {
        using var reader = AvroFileReader.Open<bench.generated.Order>(new MemoryStream(_file, writable: false), _ => bench.generated.Order.Read, s_readerOptions);
        var count = 0;
        while (reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("Read")]
    public async Task<int> AvroSharp_ReadAsync()
    {
        var reader = await AvroFileReader.OpenGenericAsync(new MemoryStream(_file, writable: false), options: s_readerOptions).ConfigureAwait(false);
        await using var disposeReader = reader.ConfigureAwait(false);
        var count = 0;
        await foreach (var _ in reader.ReadAllAsync().ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    // Reading and decompression on a background task, overlapping decoding.
    [Benchmark]
    [BenchmarkCategory("Read")]
    public async Task<int> AvroSharp_ReadPipelinedAsync()
    {
        var reader = await AvroFileReader.OpenGenericAsync(new MemoryStream(_file, writable: false), options: s_readerOptions).ConfigureAwait(false);
        await using var disposeReader = reader.ConfigureAwait(false);
        var count = 0;
        await foreach (var _ in reader.ReadAllPipelinedAsync().ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private AvroFileWriterOptions Options() => new()
    {
        Codec = OurCodec,
        SyncInterval = SyncInterval,
        LeaveOpen = true,
    };

    private byte[] WriteTo(Func<long> write)
    {
        write();
        return _output.ToArray();
    }
}
