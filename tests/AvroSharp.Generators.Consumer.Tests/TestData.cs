using System;
using System.Collections.Generic;
using System.Linq;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Sample values of the generated types.</summary>
internal static class TestData
{
    /// <summary>An order that sets every field, including nested records, unions and collections.</summary>
    public static shop.Order CreateOrder()
    {
        var sku = new shop.Sku([0xDE, 0xAD, 0xBE, 0xEF]);
        return new shop.Order
        {
            Id = 1_790_000_000_123L,
            Active = true,
            Count = -5,
            Ratio = 0.25f,
            Total = 1234.5,
            Payload = [0, 1, 255],
            CustomerName = "Ada",
            Status = shop.Status.@class,
            Sku = sku,
            Counters = [.. Enumerable.Range(0, 64).Select(i => (long)(i * i * 1000))],
            Small = [1, -1, 1000, int.MinValue],
            Weights = [1.5, -2.25, double.MaxValue],
            Ratios = [0.5f, float.Epsilon],
            Tags = ["a", "日本", ""],
            Lines = [new shop.Line { Sku = sku, Qty = 1 }, new shop.Line { Sku = sku, Qty = 2, Note = "fragile" }],
            Attributes = new() { ["x"] = 1, ["y"] = -2 },
            Groups = new() { ["g"] = ["x", "y"], ["empty"] = [] },
            Note = "leave at the door",
            Discount = null,
            Priority = 7,
            Shipping = new shop.Line { Sku = sku, Qty = 3 },
            Extra = new List<long> { 1, 2, 3 },
            Only = "only",
            Created = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000).AddTicks(1230),
#if NET6_0_OR_GREATER
            Day = new DateOnly(2024, 10, 3),
#else
            Day = new DateTime(2024, 10, 3),
#endif
            Ref = new Guid("7f1e2d3c-0000-4000-8000-000000000001"),
            Amount = 1000.00m,
            Order_ = 99,
            Schema_ = "s",
        };
    }
}
