using System;
using System.Globalization;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>The C# type of a logical type and the statements that convert it at the serializer's edge.</summary>
/// <param name="Type">The fully qualified C# type.</param>
/// <param name="Write">Builds the statement that writes a value, given the expression that holds it.</param>
/// <param name="Read">The expression that reads a value.</param>
internal sealed record LogicalValue(string Type, Func<string, string> Write, string Read);

/// <summary>Chooses the <see cref="LogicalValue"/> for a schema under the configured <see cref="LogicalTypeMapping"/>.</summary>
internal static class LogicalTypes
{
    private const string Values = "global::AvroSharp.Serialization.AvroLogicalValues";

    // System.Decimal holds 28-29 significant digits; larger precisions keep their bytes.
    private const int MaxDecimalPrecision = 28;

    /// <summary>Gets the mapping for <paramref name="schema"/>, or <see langword="null"/> when it keeps its underlying type.</summary>
    public static LogicalValue? For(AvroSchema schema, CodeGenOptions options)
    {
        if (options.LogicalTypes == LogicalTypeMapping.Raw || schema.LogicalType is not { } logical)
        {
            return null;
        }

        var dateOnly = options.TargetHasDateOnly;
        return logical.Kind switch
        {
            AvroLogicalTypeKind.Date => dateOnly
                ? new("global::System.DateOnly", e => $"writer.WriteInt({Values}.DaysFromDate({e}.ToDateTime(global::System.TimeOnly.MinValue)));", $"global::System.DateOnly.FromDateTime({Values}.DateFromDays(reader.ReadInt()))")
                : new("global::System.DateTime", e => $"writer.WriteInt({Values}.DaysFromDate({e}));", $"{Values}.DateFromDays(reader.ReadInt())"),
            AvroLogicalTypeKind.TimeMillis => Time(dateOnly, "Milliseconds", "Int"),
            AvroLogicalTypeKind.TimeMicros => Time(dateOnly, "Microseconds", "Long"),
            AvroLogicalTypeKind.TimestampMillis => Instant("global::System.DateTimeOffset", "Timestamp", "Milliseconds"),
            AvroLogicalTypeKind.TimestampMicros => Instant("global::System.DateTimeOffset", "Timestamp", "Microseconds"),
            AvroLogicalTypeKind.LocalTimestampMillis => Instant("global::System.DateTime", "LocalTimestamp", "Milliseconds"),
            AvroLogicalTypeKind.LocalTimestampMicros => Instant("global::System.DateTime", "LocalTimestamp", "Microseconds"),
            AvroLogicalTypeKind.Uuid => schema is FixedSchema
                ? new("global::System.Guid", e => $"{Values}.WriteUuidFixed(ref writer, {e});", $"{Values}.ReadUuidFixed(ref reader)")
                : new("global::System.Guid", e => $"{Values}.WriteUuidString(ref writer, {e});", $"{Values}.ReadUuidString(ref reader)"),
            AvroLogicalTypeKind.Decimal when logical is DecimalLogicalType { Precision: <= MaxDecimalPrecision } dec => Decimal(schema, dec),
            _ => null,
        };
    }

    private static LogicalValue Time(bool timeOnly, string unit, string wire) => timeOnly
        ? new("global::System.TimeOnly", e => $"writer.Write{wire}({Values}.{unit}FromTime({e}.ToTimeSpan()));", $"global::System.TimeOnly.FromTimeSpan({Values}.TimeFrom{unit}(reader.Read{wire}()))")
        : new("global::System.TimeSpan", e => $"writer.Write{wire}({Values}.{unit}FromTime({e}));", $"{Values}.TimeFrom{unit}(reader.Read{wire}())");

    private static LogicalValue Instant(string type, string kind, string unit) =>
        new(type, e => $"writer.WriteLong({Values}.{unit}From{kind}({e}));", $"{Values}.{kind}From{unit}(reader.ReadLong())");

    private static LogicalValue Decimal(AvroSchema schema, DecimalLogicalType dec)
    {
        var scale = dec.Scale.ToString(CultureInfo.InvariantCulture);
        var precision = dec.Precision.ToString(CultureInfo.InvariantCulture);
        if (schema is FixedSchema fixedSchema)
        {
            var size = fixedSchema.Size.ToString(CultureInfo.InvariantCulture);
            return new("decimal", e => $"{Values}.WriteDecimalFixed(ref writer, {e}, {scale}, {precision}, {size});", $"{Values}.ReadDecimalFixed(ref reader, {scale}, {size})");
        }

        return new("decimal", e => $"{Values}.WriteDecimalBytes(ref writer, {e}, {scale}, {precision});", $"{Values}.ReadDecimalBytes(ref reader, {scale})");
    }
}
