using System;
using System.Buffers.Binary;
using System.Globalization;
using AvroSharp.IO;

namespace AvroSharp.Serialization;

/// <summary>
/// Conversions between Avro logical types and .NET types, used by generated serializers and available for the
/// generic model (whose values are the underlying types).
/// </summary>
/// <remarks>
/// <para>
/// Dates, times and timestamps written from .NET values are truncated towards negative infinity to the logical
/// type's precision (a <c>timestamp-millis</c> drops sub-millisecond ticks), as the Java implementation does. Values
/// read that do not fit the .NET type raise <see cref="AvroDataException"/>.
/// </para>
/// <para>
/// Decimals are exact: writing a value with more fractional digits than the schema's scale, or more digits than its
/// precision, raises <see cref="AvroException"/> instead of rounding.
/// </para>
/// </remarks>
public static class AvroLogicalValues
{
    private const long TicksPerMicrosecond = 10;
    private const long UnixEpochTicks = 621355968000000000; // 1970-01-01T00:00:00Z
    private const int MaxDecimalScale = 28;

    private static readonly DateTime s_epoch = new(UnixEpochTicks, DateTimeKind.Unspecified);

    // --- date: days since 1970-01-01 ---

    /// <summary>Converts a <c>date</c> (days since the Unix epoch) to a <see cref="DateTime"/> at midnight.</summary>
    /// <param name="days">The days since 1970-01-01.</param>
    public static DateTime DateFromDays(int days) =>
        days >= (DateTime.MinValue - s_epoch).Days && days <= (DateTime.MaxValue - s_epoch).Days
            ? s_epoch.AddDays(days)
            : throw new AvroDataException($"The date {days} days from 1970-01-01 is outside the range of DateTime.");

    /// <summary>Converts a date to a <c>date</c> (days since the Unix epoch); the time of day is ignored.</summary>
    /// <param name="date">The date.</param>
    public static int DaysFromDate(DateTime date) => (int)((date.Date.Ticks - UnixEpochTicks) / TimeSpan.TicksPerDay);

    // --- time-millis / time-micros: time of day ---

    /// <summary>Converts a <c>time-millis</c> (milliseconds after midnight) to a <see cref="TimeSpan"/>.</summary>
    /// <param name="milliseconds">The milliseconds after midnight.</param>
    public static TimeSpan TimeFromMilliseconds(int milliseconds) => TimeOfDay(milliseconds * TimeSpan.TicksPerMillisecond, "time-millis");

    /// <summary>Converts a <c>time-micros</c> (microseconds after midnight) to a <see cref="TimeSpan"/>.</summary>
    /// <param name="microseconds">The microseconds after midnight.</param>
    public static TimeSpan TimeFromMicroseconds(long microseconds) =>
        microseconds is >= 0 and < TimeSpan.TicksPerDay / TicksPerMicrosecond
            ? TimeSpan.FromTicks(microseconds * TicksPerMicrosecond)
            : throw new AvroDataException($"The time-micros value {microseconds} is not a time of day.");

    /// <summary>Converts a time of day to a <c>time-millis</c>, truncating sub-millisecond ticks.</summary>
    /// <param name="time">A time of day, from zero up to (not including) 24 hours.</param>
    public static int MillisecondsFromTime(TimeSpan time) => (int)(CheckTimeOfDay(time).Ticks / TimeSpan.TicksPerMillisecond);

    /// <summary>Converts a time of day to a <c>time-micros</c>, truncating sub-microsecond ticks.</summary>
    /// <param name="time">A time of day, from zero up to (not including) 24 hours.</param>
    public static long MicrosecondsFromTime(TimeSpan time) => CheckTimeOfDay(time).Ticks / TicksPerMicrosecond;

    // --- timestamp-millis / timestamp-micros: UTC instants ---

    /// <summary>Converts a <c>timestamp-millis</c> (milliseconds since the Unix epoch, UTC) to a <see cref="DateTimeOffset"/>.</summary>
    /// <param name="milliseconds">The milliseconds since 1970-01-01T00:00:00Z.</param>
    public static DateTimeOffset TimestampFromMilliseconds(long milliseconds) =>
        new(EpochTicks(milliseconds, TimeSpan.TicksPerMillisecond, "timestamp-millis"), TimeSpan.Zero);

    /// <summary>Converts a <c>timestamp-micros</c> (microseconds since the Unix epoch, UTC) to a <see cref="DateTimeOffset"/>.</summary>
    /// <param name="microseconds">The microseconds since 1970-01-01T00:00:00Z.</param>
    public static DateTimeOffset TimestampFromMicroseconds(long microseconds) =>
        new(EpochTicks(microseconds, TicksPerMicrosecond, "timestamp-micros"), TimeSpan.Zero);

    /// <summary>Converts an instant to a <c>timestamp-millis</c>, truncating sub-millisecond ticks.</summary>
    /// <param name="timestamp">The instant; its offset is taken into account.</param>
    public static long MillisecondsFromTimestamp(DateTimeOffset timestamp) => FloorDivide(timestamp.UtcTicks - UnixEpochTicks, TimeSpan.TicksPerMillisecond);

    /// <summary>Converts an instant to a <c>timestamp-micros</c>, truncating sub-microsecond ticks.</summary>
    /// <param name="timestamp">The instant; its offset is taken into account.</param>
    public static long MicrosecondsFromTimestamp(DateTimeOffset timestamp) => FloorDivide(timestamp.UtcTicks - UnixEpochTicks, TicksPerMicrosecond);

    // --- local-timestamp-millis / local-timestamp-micros: wall-clock times without a time zone ---

    /// <summary>Converts a <c>local-timestamp-millis</c> to a <see cref="DateTime"/> of unspecified kind.</summary>
    /// <param name="milliseconds">The milliseconds since 1970-01-01T00:00:00, local time.</param>
    public static DateTime LocalTimestampFromMilliseconds(long milliseconds) =>
        new(EpochTicks(milliseconds, TimeSpan.TicksPerMillisecond, "local-timestamp-millis"), DateTimeKind.Unspecified);

    /// <summary>Converts a <c>local-timestamp-micros</c> to a <see cref="DateTime"/> of unspecified kind.</summary>
    /// <param name="microseconds">The microseconds since 1970-01-01T00:00:00, local time.</param>
    public static DateTime LocalTimestampFromMicroseconds(long microseconds) =>
        new(EpochTicks(microseconds, TicksPerMicrosecond, "local-timestamp-micros"), DateTimeKind.Unspecified);

    /// <summary>Converts a wall-clock time to a <c>local-timestamp-millis</c>; its <see cref="DateTime.Kind"/> is ignored.</summary>
    /// <param name="timestamp">The wall-clock time.</param>
    public static long MillisecondsFromLocalTimestamp(DateTime timestamp) => FloorDivide(timestamp.Ticks - UnixEpochTicks, TimeSpan.TicksPerMillisecond);

    /// <summary>Converts a wall-clock time to a <c>local-timestamp-micros</c>; its <see cref="DateTime.Kind"/> is ignored.</summary>
    /// <param name="timestamp">The wall-clock time.</param>
    public static long MicrosecondsFromLocalTimestamp(DateTime timestamp) => FloorDivide(timestamp.Ticks - UnixEpochTicks, TicksPerMicrosecond);

    // --- uuid ---

    /// <summary>Reads a <c>uuid</c> on <c>string</c> (the 36-character hyphenated form).</summary>
    /// <param name="reader">The source.</param>
    public static Guid ReadUuidString(ref AvroReader reader)
    {
        var text = reader.ReadString();
        return Guid.TryParseExact(text, "D", out var uuid)
            ? uuid
            : throw new AvroDataException($"'{text}' is not a UUID in the form xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.");
    }

    /// <summary>Writes a <c>uuid</c> on <c>string</c>, in lowercase hyphenated form.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="uuid">The UUID.</param>
    public static void WriteUuidString(ref AvroWriter writer, Guid uuid)
    {
#if NET8_0_OR_GREATER
        Span<char> text = stackalloc char[36];
        uuid.TryFormat(text, out _, "D");
        writer.WriteString(text);
#else
        writer.WriteString(uuid.ToString("D", CultureInfo.InvariantCulture));
#endif
    }

    /// <summary>Reads a <c>uuid</c> on <c>fixed(16)</c>: the 16 bytes in RFC 4122 (big-endian) order.</summary>
    /// <param name="reader">The source.</param>
    public static Guid ReadUuidFixed(ref AvroReader reader)
    {
        var bytes = reader.ReadFixedSpan(16);
#if NET8_0_OR_GREATER
        return new Guid(bytes, bigEndian: true);
#else
        // Guid's byte layout is little-endian in its first three fields.
        return new Guid(
            BinaryPrimitives.ReadInt32BigEndian(bytes),
            BinaryPrimitives.ReadInt16BigEndian(bytes[4..]),
            BinaryPrimitives.ReadInt16BigEndian(bytes[6..]),
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
#endif
    }

    /// <summary>Writes a <c>uuid</c> on <c>fixed(16)</c>, in RFC 4122 (big-endian) byte order.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="uuid">The UUID.</param>
    public static void WriteUuidFixed(ref AvroWriter writer, Guid uuid)
    {
        Span<byte> bytes = stackalloc byte[16];
#if NET8_0_OR_GREATER
        uuid.TryWriteBytes(bytes, bigEndian: true, out _);
#else
        uuid.ToByteArray().CopyTo(bytes);
        bytes[..4].Reverse();
        bytes.Slice(4, 2).Reverse();
        bytes.Slice(6, 2).Reverse();
#endif
        writer.WriteFixed(bytes);
    }

    // --- decimal ---

    /// <summary>Reads a <c>decimal</c> on <c>bytes</c>: a two's-complement big-endian unscaled integer.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="scale">The schema's scale (0 to 28).</param>
    public static decimal ReadDecimalBytes(ref AvroReader reader, int scale) => DecimalFromUnscaled(reader.ReadBytesSpan(), scale);

    /// <summary>Reads a <c>decimal</c> on <c>fixed</c>: a two's-complement big-endian unscaled integer of the fixed size.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="scale">The schema's scale (0 to 28).</param>
    /// <param name="size">The fixed type's size.</param>
    public static decimal ReadDecimalFixed(ref AvroReader reader, int scale, int size) => DecimalFromUnscaled(reader.ReadFixedSpan(size), scale);

    /// <summary>Writes a <c>decimal</c> on <c>bytes</c>, using the fewest bytes that hold the unscaled value.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value; it must have at most <paramref name="scale"/> fractional digits and <paramref name="precision"/> digits.</param>
    /// <param name="scale">The schema's scale.</param>
    /// <param name="precision">The schema's precision.</param>
    public static void WriteDecimalBytes(ref AvroWriter writer, decimal value, int scale, int precision)
    {
        Span<byte> bytes = stackalloc byte[16];
        var length = Unscaled(value, scale, precision, bytes);
        writer.WriteBytes(bytes[(16 - length)..]);
    }

    /// <summary>Writes a <c>decimal</c> on <c>fixed</c>, sign-extended to the fixed size.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value; it must have at most <paramref name="scale"/> fractional digits and <paramref name="precision"/> digits.</param>
    /// <param name="scale">The schema's scale.</param>
    /// <param name="precision">The schema's precision.</param>
    /// <param name="size">The fixed type's size.</param>
    public static void WriteDecimalFixed(ref AvroWriter writer, decimal value, int scale, int precision, int size)
    {
        Span<byte> bytes = stackalloc byte[16];
        var length = Unscaled(value, scale, precision, bytes);
        if (length > size)
        {
            throw new AvroException($"The decimal {value.ToString(CultureInfo.InvariantCulture)} does not fit in {size} bytes.");
        }

        Span<byte> destination = size <= 64 ? stackalloc byte[64] : new byte[size];
        destination = destination[..size];
        destination.Fill(value < 0 ? (byte)0xFF : (byte)0);
        bytes[(16 - Math.Min(size, 16))..].CopyTo(destination[Math.Max(0, size - 16)..]);
        writer.WriteFixed(destination);
    }

    private static TimeSpan TimeOfDay(long ticks, string type) =>
        ticks is >= 0 and < TimeSpan.TicksPerDay
            ? TimeSpan.FromTicks(ticks)
            : throw new AvroDataException($"The {type} value is not a time of day.");

    private static TimeSpan CheckTimeOfDay(TimeSpan time) =>
        time.Ticks is >= 0 and < TimeSpan.TicksPerDay
            ? time
            : throw new AvroException($"{time} is not a time of day (from zero up to 24 hours).");

    private static long EpochTicks(long units, long ticksPerUnit, string type)
    {
        // DateTime covers years 1 to 9999; anything outside is not representable.
        const long MinUnitsTicks = -UnixEpochTicks;
        var maxUnitsTicks = DateTime.MaxValue.Ticks - UnixEpochTicks;
        if (units < MinUnitsTicks / ticksPerUnit || units > maxUnitsTicks / ticksPerUnit)
        {
            throw new AvroDataException($"The {type} value {units} is outside the range of DateTime.");
        }

        return UnixEpochTicks + (units * ticksPerUnit);
    }

    private static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>Interprets two's-complement big-endian bytes as an unscaled integer and applies the scale.</summary>
    private static decimal DecimalFromUnscaled(ReadOnlySpan<byte> bytes, int scale)
    {
        if (scale is < 0 or > MaxDecimalScale)
        {
            throw new AvroDataException($"A decimal scale of {scale} is outside the range of System.Decimal (0 to 28).");
        }

        // The unscaled value is a two's-complement integer, which has at least one byte (Java's BigInteger rejects none).
        if (bytes.IsEmpty)
        {
            throw new AvroDataException("A decimal has no bytes; its unscaled value needs at least one.");
        }

        var negative = bytes[0] >= 0x80;

        // Leading bytes that only repeat the sign carry no value.
        var extension = negative ? (byte)0xFF : (byte)0;
        while (bytes.Length > 12 && bytes[0] == extension)
        {
            bytes = bytes[1..];
        }

        if (bytes.Length > 13 || (bytes.Length == 13 && bytes[0] != extension))
        {
            throw new AvroDataException("A decimal value does not fit in System.Decimal (96 bits).");
        }

        // Sign-extend into 16 bytes, read as a 128-bit two's-complement number, then take the magnitude.
        Span<byte> wide = stackalloc byte[16];
        wide.Fill(extension);
        bytes.CopyTo(wide[(16 - bytes.Length)..]);
        var high = BinaryPrimitives.ReadUInt64BigEndian(wide);
        var low = BinaryPrimitives.ReadUInt64BigEndian(wide[8..]);
        if (negative)
        {
            high = ~high;
            low = ~low + 1;
            if (low == 0)
            {
                high++;
            }
        }

        if (high > uint.MaxValue)
        {
            throw new AvroDataException("A decimal value does not fit in System.Decimal (96 bits).");
        }

        return new decimal((int)(uint)low, (int)(uint)(low >> 32), (int)(uint)high, negative, (byte)scale);
    }

    /// <summary>
    /// Writes the unscaled value of <paramref name="value"/> at <paramref name="scale"/> as minimal two's-complement
    /// big-endian bytes at the end of <paramref name="destination"/> (16 bytes) and returns their length.
    /// </summary>
    private static int Unscaled(decimal value, int scale, int precision, Span<byte> destination)
    {
        if (scale is < 0 or > MaxDecimalScale || precision is < 1 or > MaxDecimalScale)
        {
            throw new AvroException($"A decimal(precision {precision}, scale {scale}) is outside the range of System.Decimal.");
        }

        if (decimal.Round(value, scale) != value)
        {
            throw new AvroException($"The decimal {value.ToString(CultureInfo.InvariantCulture)} has more than {scale} fractional digits.");
        }

        decimal unscaled;
        try
        {
            unscaled = decimal.Truncate(value * Pow10(scale));
        }
        catch (OverflowException ex)
        {
            throw new AvroException($"The decimal {value.ToString(CultureInfo.InvariantCulture)} has more than {precision} digits.", ex);
        }

        if (Math.Abs(unscaled) >= Pow10(precision))
        {
            throw new AvroException($"The decimal {value.ToString(CultureInfo.InvariantCulture)} has more than {precision} digits.");
        }

        var bits = decimal.GetBits(unscaled);
        ulong low = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        ulong high = (uint)bits[2];
        if (unscaled < 0)
        {
            high = ~high;
            low = ~low + 1;
            if (low == 0)
            {
                high++;
            }
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low);

        // Drop leading bytes that only repeat the sign, keeping the byte that carries it.
        var start = 0;
        while (start < 15 && RepeatsSign(destination[start], destination[start + 1]))
        {
            start++;
        }

        return 16 - start;
    }

    // A leading byte of a two's-complement number carries no value when it only repeats the sign of the next byte:
    // 0x00 before a byte whose top bit is clear, or 0xFF before one whose top bit is set.
    private static bool RepeatsSign(byte leading, byte next) =>
        leading == 0 ? next < 0x80 : leading == 0xFF && next >= 0x80;

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
