using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent.Tests;
using BenchmarkDotNet.Attributes;
using bench.shop;
using Chr.Avro.Confluent;
using Confluent.Kafka;
using Confluent.SchemaRegistry.Serdes;
using ChrSerializer = Chr.Avro.Confluent.AsyncSchemaRegistrySerializer<AvroSharp.Confluent.Benchmarks.ChrOrder>;
using ChrDeserializer = Chr.Avro.Confluent.AsyncSchemaRegistryDeserializer<AvroSharp.Confluent.Benchmarks.ChrOrder>;

namespace AvroSharp.Confluent.Benchmarks;

/// <summary>
/// One message at a time: AvroSharp.Confluent against Confluent's Avro serde and Chr.Avro.Confluent, writing and
/// reading the same order, with and without <c>use.latest.version</c>. Chr.Avro always writes with the subject's latest
/// schema (it has no other mode without registering its own), so its rows are the same in both.
/// </summary>
[MemoryDiagnoser]
public class SerdeBenchmarks
{
    private const string Topic = "orders";
    private static readonly SerializationContext Context = new(MessageComponentType.Value, Topic);

    private InMemorySchemaRegistry _registry = null!;
    private Order _order = null!;
    private ChrOrder _chrOrder = null!;
    private byte[] _message = null!;
    private AvroSharpSerializer<Order> _avroSharp = null!;
    private AvroSerializer<Order> _confluent = null!;
    private ChrSerializer _chr = null!;
    private AvroSharpDeserializer<Order> _avroSharpReader = null!;
    private AvroDeserializer<Order> _confluentReader = null!;
    private ChrDeserializer _chrReader = null!;

    [Params(false, true)]
    public bool UseLatest { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _registry = new InMemorySchemaRegistry();
        await _registry.RegisterSchemaAsync(Topic + "-value", Order._SCHEMA.ToString());
        (_order, _chrOrder) = (Orders.New(), Orders.NewChr());
        var config = new AvroSharpSerializerConfig { UseLatestVersion = UseLatest, AutoRegisterSchemas = !UseLatest };
        _avroSharp = new AvroSharpSerializer<Order>(_registry, config);
        _confluent = new AvroSerializer<Order>(_registry, new AvroSerializerConfig { UseLatestVersion = UseLatest, AutoRegisterSchemas = !UseLatest });
        _chr = new ChrSerializer(_registry, AutomaticRegistrationBehavior.Never);
        _avroSharpReader = new AvroSharpDeserializer<Order>(_registry, new AvroSharpDeserializerConfig { UseLatestVersion = UseLatest });
        _confluentReader = new AvroDeserializer<Order>(_registry, new AvroDeserializerConfig { UseLatestVersion = UseLatest });
        _chrReader = new ChrDeserializer(_registry);

        _message = (await _avroSharp.SerializeAsync(_order, Context))!;
        var confluent = await _confluent.SerializeAsync(_order, Context);
        var chr = await _chr.SerializeAsync(_chrOrder, Context);
        Check(_message.SequenceEqual(confluent), "Confluent's bytes differ");
        Check(_message.SequenceEqual(chr), "Chr.Avro's bytes differ");
        Check((await _chrReader.DeserializeAsync(_message, false, Context)).Customer == "Ada", "Chr.Avro reads wrongly");
        Check((await _confluentReader.DeserializeAsync(_message, false, Context)).Customer == "Ada", "Confluent reads wrongly");
        Check((await _avroSharpReader.DeserializeAsync(_message, false, Context)).Customer == "Ada", "AvroSharp reads wrongly");
    }

    [GlobalCleanup]
    public void Cleanup() => _registry.Dispose();

    [Benchmark(Baseline = true), BenchmarkCategory("Serialize")]
    public Task<byte[]> Confluent_Serialize() => _confluent.SerializeAsync(_order, Context);

    [Benchmark, BenchmarkCategory("Serialize")]
    public Task<byte[]> ChrAvro_Serialize() => _chr.SerializeAsync(_chrOrder, Context);

    [Benchmark, BenchmarkCategory("Serialize")]
    public Task<byte[]?> AvroSharp_Serialize() => _avroSharp.SerializeAsync(_order, Context);

    [Benchmark, BenchmarkCategory("Serialize")]
    public byte[]? AvroSharp_SerializeSync() => _avroSharp.Serialize(_order, Context);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public Task<Order> Confluent_Deserialize() => _confluentReader.DeserializeAsync(_message, false, Context);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public Task<ChrOrder> ChrAvro_Deserialize() => _chrReader.DeserializeAsync(_message, false, Context);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public Task<Order> AvroSharp_Deserialize() => _avroSharpReader.DeserializeAsync(_message, false, Context);

    [Benchmark, BenchmarkCategory("Deserialize")]
    public Order AvroSharp_DeserializeSync() => _avroSharpReader.Deserialize(_message, false, Context);

    internal static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>Several producers and consumers sharing one serializer and deserializer, as threads of one application do.</summary>
[MemoryDiagnoser]
public class ConcurrentSerdeBenchmarks
{
    private const int Messages = 4096;
    private static readonly SerializationContext Context = new(MessageComponentType.Value, "orders");

    private InMemorySchemaRegistry _registry = null!;
    private Order _order = null!;
    private byte[] _message = null!;
    private AvroSharpSerializer<Order> _avroSharp = null!;
    private AvroSerializer<Order> _confluent = null!;
    private AvroSharpDeserializer<Order> _avroSharpReader = null!;
    private AvroDeserializer<Order> _confluentReader = null!;

    [Params(8)]
    public int Threads { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _registry = new InMemorySchemaRegistry();
        _order = Orders.New();
        _avroSharp = new AvroSharpSerializer<Order>(_registry);
        _confluent = new AvroSerializer<Order>(_registry);
        _avroSharpReader = new AvroSharpDeserializer<Order>(_registry);
        _confluentReader = new AvroDeserializer<Order>(_registry);
        _message = (await _avroSharp.SerializeAsync(_order, Context))!;
        await _confluentReader.DeserializeAsync(_message, false, Context);
    }

    [GlobalCleanup]
    public void Cleanup() => _registry.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = Messages), BenchmarkCategory("Serialize")]
    public Task Confluent_Serialize() => Run(() => _confluent.SerializeAsync(_order, Context));

    [Benchmark(OperationsPerInvoke = Messages), BenchmarkCategory("Serialize")]
    public Task AvroSharp_Serialize() => Run(() => _avroSharp.SerializeAsync(_order, Context));

    [Benchmark(OperationsPerInvoke = Messages), BenchmarkCategory("Deserialize")]
    public Task Confluent_Deserialize() => Run(() => _confluentReader.DeserializeAsync(_message, false, Context));

    [Benchmark(OperationsPerInvoke = Messages), BenchmarkCategory("Deserialize")]
    public Task AvroSharp_Deserialize() => Run(() => _avroSharpReader.DeserializeAsync(_message, false, Context));

    private Task Run<TResult>(Func<Task<TResult>> operation) =>
        Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < Messages / Threads; i++)
            {
                await operation().ConfigureAwait(false);
            }
        })));
}

internal static class Orders
{
    public static Order New() => new()
    {
        Id = new Guid("0b7c6f0e-2d55-4a8e-9f6b-6f1d2c3b4a59"),
        Customer = "Ada",
        PlacedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        Total = new Avro.AvroDecimal(129.95m),
        Status = Status.PAID,
        Items = [new() { Sku = "SKU-1", Quantity = 2 }, new() { Sku = "SKU-2", Quantity = 1 }],
        Tags = new Dictionary<string, string> { ["channel"] = "web" },
        Note = "gift",
    };

    public static ChrOrder NewChr() => new()
    {
        Id = new Guid("0b7c6f0e-2d55-4a8e-9f6b-6f1d2c3b4a59"),
        Customer = "Ada",
        PlacedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        Total = 129.95m,
        Status = ChrStatus.PAID,
        Items = [new() { Sku = "SKU-1", Quantity = 2 }, new() { Sku = "SKU-2", Quantity = 1 }],
        Tags = new Dictionary<string, string> { ["channel"] = "web" },
        Note = "gift",
    };
}

/// <summary>The order as Chr.Avro maps it: a plain class, matched to the schema by member names.</summary>
public sealed class ChrOrder
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public DateTime PlacedAt { get; set; }

    public decimal Total { get; set; }

    public ChrStatus Status { get; set; }

    public List<ChrItem> Items { get; set; } = [];

    public Dictionary<string, string> Tags { get; set; } = [];

    public string? Note { get; set; }
}

public sealed class ChrItem
{
    public string Sku { get; set; } = "";

    public int Quantity { get; set; }
}

#pragma warning disable CA1707 // The schema's symbols.
public enum ChrStatus
{
    NEW,
    PAID,
    SHIPPED,
}
#pragma warning restore CA1707
