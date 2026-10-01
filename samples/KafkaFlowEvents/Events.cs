using System;
using AvroSharp.Serialization;

// Field names in camelCase, as schemas shared with Java and other languages usually have them.
[assembly: AvroSerializableDefaults(FieldNames = AvroNaming.CamelCase)]

namespace Shop.Events;

/// <summary>An order was placed.</summary>
[AvroSerializable]
internal sealed partial class OrderPlaced
{
    public long OrderId { get; set; }

    public string Customer { get; set; } = "";

    [AvroDecimal(12, 2)]
    public decimal Total { get; set; }

    public DateTimeOffset PlacedAt { get; set; }
}

/// <summary>An order was shipped.</summary>
[AvroSerializable]
internal sealed partial class OrderShipped
{
    public long OrderId { get; set; }

    public string Carrier { get; set; } = "";

    public string? TrackingNumber { get; set; }
}
