using System;

namespace AvroSharp.Schemas;

/// <summary>The logical types defined by the Avro 1.12 specification.</summary>
public enum AvroLogicalTypeKind
{
    /// <summary><c>decimal</c> on <c>bytes</c> or <c>fixed</c>, with a precision and scale.</summary>
    Decimal,

    /// <summary><c>big-decimal</c> on <c>bytes</c>; the scale is stored with each value.</summary>
    BigDecimal,

    /// <summary><c>uuid</c> on <c>string</c> or <c>fixed</c> of size 16.</summary>
    Uuid,

    /// <summary><c>date</c> on <c>int</c>: days since the Unix epoch.</summary>
    Date,

    /// <summary><c>time-millis</c> on <c>int</c>: milliseconds after midnight.</summary>
    TimeMillis,

    /// <summary><c>time-micros</c> on <c>long</c>: microseconds after midnight.</summary>
    TimeMicros,

    /// <summary><c>timestamp-millis</c> on <c>long</c>: milliseconds since the Unix epoch (UTC).</summary>
    TimestampMillis,

    /// <summary><c>timestamp-micros</c> on <c>long</c>: microseconds since the Unix epoch (UTC).</summary>
    TimestampMicros,

    /// <summary><c>timestamp-nanos</c> on <c>long</c>: nanoseconds since the Unix epoch (UTC).</summary>
    TimestampNanos,

    /// <summary><c>local-timestamp-millis</c> on <c>long</c>: milliseconds since 1970-01-01 in local time.</summary>
    LocalTimestampMillis,

    /// <summary><c>local-timestamp-micros</c> on <c>long</c>: microseconds since 1970-01-01 in local time.</summary>
    LocalTimestampMicros,

    /// <summary><c>local-timestamp-nanos</c> on <c>long</c>: nanoseconds since 1970-01-01 in local time.</summary>
    LocalTimestampNanos,

    /// <summary><c>duration</c> on <c>fixed</c> of size 12: months, days and milliseconds.</summary>
    Duration,
}

/// <summary>
/// A logical type annotating a primitive or fixed schema. Parameterless logical types are singletons;
/// <c>decimal</c> is represented by <see cref="DecimalLogicalType"/>.
/// </summary>
/// <remarks>
/// As the specification requires, unknown or invalid logical types are ignored when parsing: the schema then has
/// no <see cref="AvroSchema.LogicalType"/>, and the original attributes are kept in <see cref="AvroSchema.Properties"/>.
/// </remarks>
public abstract class AvroLogicalType
{
    private protected AvroLogicalType(AvroLogicalTypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>Gets the <c>big-decimal</c> logical type.</summary>
    public static AvroLogicalType BigDecimal { get; } = new SimpleLogicalType(AvroLogicalTypeKind.BigDecimal, "big-decimal");

    /// <summary>Gets the <c>uuid</c> logical type.</summary>
    public static AvroLogicalType Uuid { get; } = new SimpleLogicalType(AvroLogicalTypeKind.Uuid, "uuid");

    /// <summary>Gets the <c>date</c> logical type.</summary>
    public static AvroLogicalType Date { get; } = new SimpleLogicalType(AvroLogicalTypeKind.Date, "date");

    /// <summary>Gets the <c>time-millis</c> logical type.</summary>
    public static AvroLogicalType TimeMillis { get; } = new SimpleLogicalType(AvroLogicalTypeKind.TimeMillis, "time-millis");

    /// <summary>Gets the <c>time-micros</c> logical type.</summary>
    public static AvroLogicalType TimeMicros { get; } = new SimpleLogicalType(AvroLogicalTypeKind.TimeMicros, "time-micros");

    /// <summary>Gets the <c>timestamp-millis</c> logical type.</summary>
    public static AvroLogicalType TimestampMillis { get; } = new SimpleLogicalType(AvroLogicalTypeKind.TimestampMillis, "timestamp-millis");

    /// <summary>Gets the <c>timestamp-micros</c> logical type.</summary>
    public static AvroLogicalType TimestampMicros { get; } = new SimpleLogicalType(AvroLogicalTypeKind.TimestampMicros, "timestamp-micros");

    /// <summary>Gets the <c>timestamp-nanos</c> logical type.</summary>
    public static AvroLogicalType TimestampNanos { get; } = new SimpleLogicalType(AvroLogicalTypeKind.TimestampNanos, "timestamp-nanos");

    /// <summary>Gets the <c>local-timestamp-millis</c> logical type.</summary>
    public static AvroLogicalType LocalTimestampMillis { get; } = new SimpleLogicalType(AvroLogicalTypeKind.LocalTimestampMillis, "local-timestamp-millis");

    /// <summary>Gets the <c>local-timestamp-micros</c> logical type.</summary>
    public static AvroLogicalType LocalTimestampMicros { get; } = new SimpleLogicalType(AvroLogicalTypeKind.LocalTimestampMicros, "local-timestamp-micros");

    /// <summary>Gets the <c>local-timestamp-nanos</c> logical type.</summary>
    public static AvroLogicalType LocalTimestampNanos { get; } = new SimpleLogicalType(AvroLogicalTypeKind.LocalTimestampNanos, "local-timestamp-nanos");

    /// <summary>Gets the <c>duration</c> logical type.</summary>
    public static AvroLogicalType Duration { get; } = new SimpleLogicalType(AvroLogicalTypeKind.Duration, "duration");

    /// <summary>Gets the kind of logical type.</summary>
    public AvroLogicalTypeKind Kind { get; }

    /// <summary>Gets the name used in the <c>logicalType</c> attribute.</summary>
    public string Name { get; }

    /// <summary>Creates a <c>decimal</c> logical type.</summary>
    /// <param name="precision">The maximum number of digits; must be greater than zero.</param>
    /// <param name="scale">The number of digits after the decimal point; between zero and <paramref name="precision"/>.</param>
    public static DecimalLogicalType Decimal(int precision, int scale = 0) => new(precision, scale);

    /// <summary>
    /// Returns <see langword="true"/> when this logical type may annotate <paramref name="schema"/>
    /// (for example <c>date</c> on <c>int</c>, or a <c>decimal</c> whose precision fits the fixed size).
    /// </summary>
    /// <param name="schema">The underlying schema, which is not required to carry this logical type already.</param>
    public abstract bool IsValidFor(AvroSchema schema);

    /// <inheritdoc />
    public override string ToString() => Name;

    internal static AvroLogicalType? FromName(string name) => name switch
    {
        "big-decimal" => BigDecimal,
        "uuid" => Uuid,
        "date" => Date,
        "time-millis" => TimeMillis,
        "time-micros" => TimeMicros,
        "timestamp-millis" => TimestampMillis,
        "timestamp-micros" => TimestampMicros,
        "timestamp-nanos" => TimestampNanos,
        "local-timestamp-millis" => LocalTimestampMillis,
        "local-timestamp-micros" => LocalTimestampMicros,
        "local-timestamp-nanos" => LocalTimestampNanos,
        "duration" => Duration,
        _ => null,
    };

    private sealed class SimpleLogicalType(AvroLogicalTypeKind kind, string name) : AvroLogicalType(kind, name)
    {
        public override bool IsValidFor(AvroSchema schema) => Kind switch
        {
            AvroLogicalTypeKind.BigDecimal => schema.Type == AvroSchemaType.Bytes,
            AvroLogicalTypeKind.Uuid => schema.Type == AvroSchemaType.String || schema is FixedSchema { Size: 16 },
            AvroLogicalTypeKind.Date or AvroLogicalTypeKind.TimeMillis => schema.Type == AvroSchemaType.Int,
            AvroLogicalTypeKind.Duration => schema is FixedSchema { Size: 12 },
            _ => schema.Type == AvroSchemaType.Long,
        };
    }
}

/// <summary>The <c>decimal</c> logical type: an arbitrary-precision decimal stored as a scaled two's-complement integer.</summary>
public sealed class DecimalLogicalType : AvroLogicalType, IEquatable<DecimalLogicalType>
{
    internal DecimalLogicalType(int precision, int scale)
        : base(AvroLogicalTypeKind.Decimal, "decimal")
    {
        if (precision <= 0)
        {
            throw new AvroSchemaException($"Decimal precision must be greater than zero, but was {precision}.");
        }

        if (scale < 0 || scale > precision)
        {
            throw new AvroSchemaException($"Decimal scale must be between 0 and the precision ({precision}), but was {scale}.");
        }

        Precision = precision;
        Scale = scale;
    }

    /// <summary>Gets the maximum number of digits.</summary>
    public int Precision { get; }

    /// <summary>Gets the number of digits after the decimal point.</summary>
    public int Scale { get; }

    /// <summary>
    /// Gets the largest precision a <c>fixed</c> of <paramref name="size"/> bytes can hold:
    /// <c>floor(log10(2^(8 * size - 1) - 1))</c>.
    /// </summary>
    /// <param name="size">The fixed size in bytes.</param>
    public static int MaxPrecisionForFixedSize(int size) =>
        size <= 0 ? 0 : (int)Math.Floor(Math.Log10(2) * ((8.0 * size) - 1));

    /// <inheritdoc />
    public override bool IsValidFor(AvroSchema schema) => schema switch
    {
        FixedSchema fixedSchema => Precision <= MaxPrecisionForFixedSize(fixedSchema.Size),
        _ => schema.Type == AvroSchemaType.Bytes,
    };

    /// <inheritdoc />
    public bool Equals(DecimalLogicalType? other) => other is not null && Precision == other.Precision && Scale == other.Scale;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DecimalLogicalType other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => unchecked((Precision * 397) ^ Scale);

    /// <inheritdoc />
    public override string ToString() => $"decimal({Precision},{Scale})";
}
