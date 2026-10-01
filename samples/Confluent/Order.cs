using System;
using System.Collections.Generic;
using AvroSharp.Serialization;

// Field names in camelCase, as schemas shared with Java and other languages usually have them.
[assembly: AvroSerializableDefaults(FieldNames = AvroNaming.CamelCase)]

namespace Shop
{
    /// <summary>An order, as the producer writes it.</summary>
    [AvroSerializable]
    internal sealed partial class Order
    {
        public long Id { get; set; }

        public string Customer { get; set; } = "";

        public DateTimeOffset PlacedAt { get; set; }

        [AvroDecimal(12, 2)]
        public decimal Total { get; set; }

        public List<OrderLine> Lines { get; set; } = [];
    }

    /// <summary>A line of an order.</summary>
    [AvroSerializable]
    internal sealed partial class OrderLine
    {
        public string Sku { get; set; } = "";

        public int Quantity { get; set; }
    }
}

namespace Billing
{
    /// <summary>
    /// A newer version of the same record, in another service: the same Avro name (<c>Shop.Order</c>), one field more
    /// with a default, and one field (<c>lines</c>) it doesn't need.
    /// </summary>
    [AvroSerializable(Name = "Order", Namespace = "Shop")]
    internal sealed partial class OrderV2
    {
        public long Id { get; set; }

        public string Customer { get; set; } = "";

        public DateTimeOffset PlacedAt { get; set; }

        [AvroDecimal(12, 2)]
        public decimal Total { get; set; }

        [AvroDefault("\"EUR\"")]
        public string Currency { get; set; } = "EUR";
    }
}
