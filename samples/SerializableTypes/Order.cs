using System;
using System.Collections.Generic;
using AvroSharp.Serialization;

// Field names in camelCase for every type here, as schemas read by Java and other languages usually have them.
[assembly: AvroNamingPolicy(AvroNaming.CamelCase)]

namespace Shop;

internal enum Status
{
    New,
    Paid,
    Shipped,
}

/// <summary>An order.</summary>
[AvroSerializable]
internal sealed partial class Order
{
    public long Id { get; set; }

    public string Customer { get; set; } = "";

    public DateTimeOffset PlacedAt { get; set; }

    [AvroDecimal(12, 2)]
    public decimal Total { get; set; }

    public Status Status { get; set; }

    // A settable List<T>: the generator reads into it. Collection interfaces and get-only collections come later (#31).
#pragma warning disable CA1002, CA2227, MA0016
    public List<OrderLine> Lines { get; set; } = [];
#pragma warning restore CA1002, CA2227, MA0016

    /// <summary>How the order was paid: a card or a voucher.</summary>
    [AvroUnion(typeof(Card), typeof(Voucher))]
    public object? Payment { get; set; }

    public string? Note { get; set; }

    // Not part of the schema.
    [AvroIgnore]
    public int CachedLineCount { get; set; }
}

[AvroSerializable]
internal sealed partial class OrderLine
{
    public string Sku { get; set; } = "";

    public int Quantity { get; set; }
}

[AvroSerializable]
internal sealed partial class Card
{
    public string Last4 { get; set; } = "";
}

[AvroSerializable]
internal sealed partial class Voucher
{
    public string Code { get; set; } = "";
}
