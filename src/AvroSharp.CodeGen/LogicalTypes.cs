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
        if (schema.LogicalType is not { } logical)
        {
            return null;
        }

        // Apache.Avro's specific reader calls Put with its own .NET types, so the compatibility mode always uses them.
        if (options.ApacheCompatible)
        {
            return Apache(schema, logical);
        }

        if (options.LogicalTypes == LogicalTypeMapping.Raw)
        {
            return null;
        }

        var dateOnly = options.TargetHasDateOnly;
        return logical.Kind switch
        {
            AvroLogicalTypeKind.Date => dateOnly
                // Day numbers count from 0001-01-01; 1970-01-01 is day 719,162. No DateTime or division either way.
                ? new("global::System.DateOnly", e => $"writer.WriteInt({e}.DayNumber - 719162);", $"global::System.DateOnly.FromDayNumber(global::AvroSharp.Serialization.AvroGeneratedCode.DayNumberFromDays(reader.ReadInt()))")
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

    /// <summary>
    /// Apache.Avro 1.12.2's mapping (its <c>LogicalType.GetCSharpType</c>): <c>DateTime</c> for dates and (UTC)
    /// timestamps, <c>TimeSpan</c> for times, <c>Guid</c> for string UUIDs, <c>AvroDecimal</c> for decimals of any
    /// precision. Apache has no conversion for nanosecond timestamps, and rejects <c>uuid</c> on <c>fixed</c>; those
    /// keep their underlying type.
    /// </summary>
    private static LogicalValue? Apache(AvroSchema schema, AvroLogicalType logical) => logical.Kind switch
    {
        AvroLogicalTypeKind.Date => new("global::System.DateTime", e => $"writer.WriteInt({Values}.DaysFromDate({e}));", $"{Values}.DateFromDays(reader.ReadInt())"),
        AvroLogicalTypeKind.TimeMillis => Time(timeOnly: false, "Milliseconds", "Int"),
        AvroLogicalTypeKind.TimeMicros => Time(timeOnly: false, "Microseconds", "Long"),
        AvroLogicalTypeKind.TimestampMillis => ApacheTimestamp("Milliseconds"),
        AvroLogicalTypeKind.TimestampMicros => ApacheTimestamp("Microseconds"),
        AvroLogicalTypeKind.LocalTimestampMillis => ApacheTimestamp("Milliseconds", local: true),
        AvroLogicalTypeKind.LocalTimestampMicros => ApacheTimestamp("Microseconds", local: true),
        AvroLogicalTypeKind.Uuid when schema is not FixedSchema =>
            new("global::System.Guid", e => $"{Values}.WriteUuidString(ref writer, {e});", $"{Values}.ReadUuidString(ref reader)"),
        // A decimal on fixed stays the fixed type: Apache's reader reuses the field's current value as a SpecificFixed
        // and then puts an AvroDecimal (Put accepts both). Apache's writer cannot write it at all: it converts the
        // AvroDecimal to a GenericFixed and then rejects that for not being a SpecificFixed (a known deviation).
        AvroLogicalTypeKind.Decimal when schema is not FixedSchema && logical is DecimalLogicalType dec => ApacheDecimal(schema, dec),
        _ => null,
    };

    /// <summary>
    /// Apache's timestamp conversions (measured on Apache.Avro 1.12.2): a DateTime is written with ToUniversalTime
    /// (Unspecified counts as local time) and read as UTC. A <c>local-timestamp</c> is handled the same way but read
    /// as local time (Kind Local), although the specification says it has no time zone.
    /// </summary>
    private static LogicalValue ApacheTimestamp(string unit, bool local = false) => new(
        "global::System.DateTime",
        e => $"writer.WriteLong({Values}.{unit}FromTimestamp(new global::System.DateTimeOffset({e}.ToUniversalTime())));",
        $"{Values}.TimestampFrom{unit}(reader.ReadLong()).UtcDateTime{(local ? ".ToLocalTime()" : string.Empty)}");

    private static LogicalValue ApacheDecimal(AvroSchema schema, DecimalLogicalType dec)
    {
        const string Decimals = "global::AvroSharp.Generated.ApacheDecimals";
        var scale = dec.Scale.ToString(CultureInfo.InvariantCulture);
        if (schema is FixedSchema fixedSchema)
        {
            var size = fixedSchema.Size.ToString(CultureInfo.InvariantCulture);
            return new("global::Avro.AvroDecimal", e => $"{Decimals}.WriteFixed(ref writer, {e}, {scale}, {size});", $"{Decimals}.ReadFixed(ref reader, {scale}, {size})");
        }

        return new("global::Avro.AvroDecimal", e => $"{Decimals}.WriteBytes(ref writer, {e}, {scale});", $"{Decimals}.ReadBytes(ref reader, {scale})");
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
