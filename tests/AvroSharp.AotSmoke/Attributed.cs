using System;
using System.Collections.Generic;
using AvroSharp.Serialization;

namespace smoke.attributed;

/// <summary>A type with its schema and serializers from the attribute-driven generator (#31).</summary>
[AvroSerializable(FieldNames = AvroNaming.CamelCase)]
internal sealed partial class Measurement
{
    public string Sensor { get; set; } = "";

    public double Value { get; set; }

    public DateTimeOffset TakenAt { get; set; }

    [AvroDecimal(9, 3)]
    public decimal Calibration { get; set; }

    public Guid Id { get; set; }

    public string? Note { get; set; }

#pragma warning disable CA1002, CA2227, MA0016 // The first version reads into a settable List<T>.
    public List<int> Samples { get; set; } = [];
#pragma warning restore CA1002, CA2227, MA0016

    [AvroUnion(typeof(Threshold), typeof(Range))]
    public object? Limit { get; set; }
}

[AvroSerializable]
internal sealed partial class Threshold
{
    public double Max { get; set; }
}

[AvroSerializable]
internal sealed partial class Range
{
    public double Low { get; set; }

    public double High { get; set; }
}
