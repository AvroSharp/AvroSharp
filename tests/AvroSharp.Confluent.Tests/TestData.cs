using System;
using System.Collections.Generic;
using Confluent.Kafka;
using test.shop;

namespace AvroSharp.Confluent.Tests;

internal static class TestData
{
    // The Order schema before status, tags and note were added.
    public const string OrderV1 = """
        {"type":"record","name":"Order","namespace":"test.shop","fields":[
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"customer","type":"string"},
          {"name":"placed_at","type":{"type":"long","logicalType":"timestamp-millis"}},
          {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
          {"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[
            {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]}
        """;

    public static Order NewOrder() => new()
    {
        Id = new Guid("0b7c6f0e-2d55-4a8e-9f6b-6f1d2c3b4a59"),
        Customer = "Ada",
        PlacedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        Total = new Avro.AvroDecimal(129.95m),
        Status = Status.PAID,
        Items = new List<Item> { new() { Sku = "SKU-1", Quantity = 2 }, new() { Sku = "SKU-2", Quantity = 1 } },
        Tags = new Dictionary<string, string> { ["channel"] = "web" },
        Note = "gift",
    };

    public static SerializationContext Value(string topic) => new(MessageComponentType.Value, topic);

    public static SerializationContext Key(string topic) => new(MessageComponentType.Key, topic);

    /// <summary>A topic name no other test uses, so tests sharing a registry don't see each other's subjects.</summary>
    public static string Topic() => "t-" + Guid.NewGuid().ToString("N");
}
