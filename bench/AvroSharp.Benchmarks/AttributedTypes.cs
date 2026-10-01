using System.Collections.Generic;
using AvroSharp.Serialization;

namespace bench.attributed;

// The Order of GenericRecordBenchmarks as a C# type with [AvroSerializable] (#31): the same fields and encoding as
// Schemas/order.avsc, so the typed benchmarks compare the two generators on one workload.
#pragma warning disable CA1002, CA2227, MA0016 // The first version reads into settable List<T> and Dictionary<TKey, TValue>.

// C# name AttributedOrder: Order clashes with the BenchmarkDotNet.Order namespace (CA1724). The Avro name is Order.
[AvroSerializable(Name = "Order", FieldNames = AvroNaming.CamelCase)]
public sealed partial class AttributedOrder
{
    public long Id { get; set; }

    public string Customer { get; set; } = "";

    public double Total { get; set; }

    public int Quantity { get; set; }

    public bool Paid { get; set; }

    public string? Note { get; set; }

    public List<Line> Lines { get; set; } = [];

    public List<long> Counters { get; set; } = [];

    public Dictionary<string, string> Tags { get; set; } = [];
}

[AvroSerializable(FieldNames = AvroNaming.CamelCase)]
public sealed partial class Line
{
    public string Sku { get; set; } = "";

    public int Qty { get; set; }

    public double Price { get; set; }
}
