using System;
using System.Globalization;
using System.Numerics;

namespace AvroSharp.Schemas;

/// <summary>
/// JSON numbers as Jackson prints them after parsing, which is how Apache Avro Java writes defaults and properties:
/// integers as written (<c>-0</c> as <c>0</c>), and numbers with a fraction or exponent as Java's
/// <c>Double.toString</c> prints the double they parse to. The arithmetic is exact (<see cref="BigInteger"/>), so
/// the result does not depend on the runtime's own parsing and formatting, which .NET Framework does not round
/// correctly.
/// </summary>
internal static class JavaJsonNumbers
{
    private const int MantissaBits = 52;
    private const int MinExponent = -1074; // of the least significant bit of a subnormal
    private const int MaxExponent = 971; // of the least significant bit of the largest finite double

    private static readonly BigInteger s_hidden = BigInteger.One << MantissaBits;

    public static string Format(string json)
    {
        if (json.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            return string.Equals(json, "-0", StringComparison.Ordinal) ? "0" : json;
        }

        return TryParse(json, out var negative, out var mantissa, out var exponent)
            ? FormatDouble(negative, mantissa, exponent)
            : json; // Infinite as a double: Jackson would print a non-JSON token; keep the text.
    }

    /// <summary>Formats a double as Java 19 and later do.</summary>
    public static string FormatDouble(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var biased = (int)((bits >> MantissaBits) & 0x7FF);
        var fraction = bits & ((1L << MantissaBits) - 1);
        return biased == 0
            ? FormatDouble(bits < 0, fraction, MinExponent)
            : FormatDouble(bits < 0, fraction | (1L << MantissaBits), biased - 1075);
    }

    // The value is (-1)^negative * mantissa * 2^exponent: the shortest decimal that rounds to it (two digits when one
    // would do, if two are closer), in plain notation from 10^-3 up to 10^7 and computerized scientific notation
    // (1.0E10) outside it, always with a digit after the point.
    private static string FormatDouble(bool negative, long mantissa, int exponent)
    {
        var text = new System.Text.StringBuilder(26);
        if (negative)
        {
            text.Append('-');
        }

        if (mantissa == 0)
        {
            return text.Append("0.0").ToString();
        }

        var (digits, decimalExponent) = Shortest(mantissa, exponent);
        if (decimalExponent is >= -3 and < 7)
        {
            if (decimalExponent < 0)
            {
                return text.Append("0.").Append('0', -decimalExponent - 1).Append(digits).ToString();
            }

            var integerLength = decimalExponent + 1;
            if (digits.Length > integerLength)
            {
                return text.Append(digits, 0, integerLength).Append('.').Append(digits, integerLength, digits.Length - integerLength).ToString();
            }

            return text.Append(digits).Append('0', integerLength - digits.Length).Append(".0").ToString();
        }

        text.Append(digits[0]).Append('.');
        if (digits.Length > 1)
        {
            text.Append(digits, 1, digits.Length - 1);
        }
        else
        {
            text.Append('0');
        }

        return text.Append('E').Append(decimalExponent.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    // Significant digits without trailing zeros, and the decimal exponent of the first one.
    private static (string Digits, int Exponent) Shortest(long mantissa, int exponent)
    {
        // Everything is scaled by 2^(2 - exponent) to integers: the value is 4m, and the rounding interval runs from
        // halfway to the previous double to halfway to the next (a quarter step below a power of two).
        var m = new BigInteger(mantissa);
        var scale = 2 - exponent;
        var value = m << 2;
        var upper = value + 2;
        var lower = mantissa == 1L << MantissaBits && exponent > MinExponent ? value - 1 : value - 2;
        var inclusive = (mantissa & 1) == 0; // an even mantissa wins ties when decimal text is parsed

        var k = DecimalExponent(m, exponent);
        (BigInteger Digits, int Exponent) found = default;
        var precision = 1;
        for (; precision <= 17; precision++)
        {
            if (Closest(precision) is { } hit)
            {
                found = hit;
                break;
            }
        }

        if (precision == 1)
        {
            // Java chooses among one- and two-digit decimals the closest to the value that still rounds to it.
            found = Closest(2) ?? found;
        }

        var (d, e) = found;

        var text = d.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        return (text, e);

        // The p-digit decimal nearest the value that lies in the rounding interval, if any; exponent of its first digit.
        (BigInteger Digits, int Exponent)? Closest(int digitCount)
        {
            var unitExponent = k - digitCount + 1;
            var rounded = RoundedDigits(value, scale, unitExponent);
            foreach (var candidate in new[] { rounded, rounded - 1, rounded + 1 })
            {
                if (candidate > 0 && InInterval(candidate, unitExponent))
                {
                    return (candidate, unitExponent + candidate.ToString(CultureInfo.InvariantCulture).Length - 1);
                }
            }

            return null;
        }

        bool InInterval(BigInteger candidate, int unitExponent)
        {
            // candidate * 10^unitExponent, compared with the interval bounds scaled by 2^scale.
            var (left, right) = Scaled(candidate, unitExponent, scale);
            var cmpLower = BigInteger.Compare(left, lower * right);
            var cmpUpper = BigInteger.Compare(left, upper * right);
            return inclusive ? cmpLower >= 0 && cmpUpper <= 0 : cmpLower > 0 && cmpUpper < 0;
        }
    }

    // floor(log10(m * 2^exponent)), exactly.
    private static int DecimalExponent(BigInteger m, int exponent)
    {
        var estimate = (int)Math.Floor((BigInteger.Log10(m) + (exponent * Math.Log10(2))) - 1e-9);
        while (Compare(m, exponent, estimate + 1) >= 0)
        {
            estimate++;
        }

        while (Compare(m, exponent, estimate) < 0)
        {
            estimate--;
        }

        return estimate;
    }

    // Compares m * 2^exponent with 10^power.
    private static int Compare(BigInteger m, int exponent, int power)
    {
        var left = exponent >= 0 ? m << exponent : m;
        var right = exponent >= 0 ? BigInteger.One : BigInteger.One << -exponent;
        if (power >= 0)
        {
            right *= BigInteger.Pow(10, power);
        }
        else
        {
            left *= BigInteger.Pow(10, -power);
        }

        return BigInteger.Compare(left, right);
    }

    // The value (4m, scaled by 2^scale) divided by 10^unitExponent, rounded half-even.
    private static BigInteger RoundedDigits(BigInteger value, int scale, int unitExponent)
    {
        // quotient = value / (2^scale * 10^unitExponent)
        var numerator = value;
        var denominator = scale >= 0 ? BigInteger.One << scale : BigInteger.One;
        if (scale < 0)
        {
            numerator <<= -scale;
        }

        if (unitExponent >= 0)
        {
            denominator *= BigInteger.Pow(10, unitExponent);
        }
        else
        {
            numerator *= BigInteger.Pow(10, -unitExponent);
        }

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        var cmp = BigInteger.Compare(twice, denominator);
        if (cmp > 0 || (cmp == 0 && !quotient.IsEven))
        {
            quotient++;
        }

        return quotient;
    }

    // candidate * 10^unitExponent * 2^scale as a fraction left / right, for comparison with integer bounds.
    private static (BigInteger Left, BigInteger Right) Scaled(BigInteger candidate, int unitExponent, int scale)
    {
        var left = candidate;
        var right = BigInteger.One;
        if (unitExponent >= 0)
        {
            left *= BigInteger.Pow(10, unitExponent);
        }
        else
        {
            right = BigInteger.Pow(10, -unitExponent);
        }

        if (scale >= 0)
        {
            left <<= scale;
        }
        else
        {
            right <<= -scale;
        }

        return (left, right);
    }

    // Parses JSON number text into the nearest double as mantissa * 2^exponent (round half to even); false if it
    // overflows.
    private static bool TryParse(string json, out bool negative, out long mantissa, out int exponent)
    {
        var (n, decimalExponent, digitCount) = ParseDecimal(json);
        negative = json[0] == '-';
        mantissa = 0;
        exponent = MinExponent;
        if (n.IsZero)
        {
            return true;
        }

        // Magnitudes far outside the double range need no arithmetic (and must not get any, for huge exponents).
        var magnitude = digitCount + decimalExponent;
        if (magnitude > 310)
        {
            return false;
        }

        return magnitude < -330 || ToBinary(n, (int)decimalExponent, out mantissa, out exponent);
    }

    // The decimal digits as an integer, the power of ten to multiply it by, and the number of digits.
    private static (BigInteger Digits, long Exponent, int Count) ParseDecimal(string json)
    {
        var e = json.IndexOfAny(['e', 'E']);
        var significand = e < 0 ? json : json.Substring(0, e);

        // An exponent too large for an int is out of the double range either way.
        long decimalExponent = 0;
#if NETSTANDARD2_0
        var exponentText = e < 0 ? string.Empty : json.Substring(e + 1);
#else
        var exponentText = e < 0 ? default : json.AsSpan(e + 1);
#endif
        if (e >= 0 && !long.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimalExponent))
        {
            decimalExponent = json[e + 1] == '-' ? -100_000 : 100_000;
        }

        decimalExponent = Math.Max(Math.Min(decimalExponent, 100_000), -100_000);
        var digits = new System.Text.StringBuilder(significand.Length);
        foreach (var c in significand)
        {
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
        }

        var point = significand.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0)
        {
            decimalExponent -= significand.Length - point - 1;
        }

        return (BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture), decimalExponent, digits.Length);
    }

    // n * 10^decimalExponent rounded to the nearest double; false if it overflows.
    private static bool ToBinary(BigInteger n, int decimalExponent, out long mantissa, out int exponent)
    {
        var numerator = decimalExponent >= 0 ? n * BigInteger.Pow(10, decimalExponent) : n;
        var denominator = decimalExponent >= 0 ? BigInteger.One : BigInteger.Pow(10, -decimalExponent);

        // Find the exponent that puts the quotient in [2^52, 2^53), or the subnormal exponent.
        var binaryExponent = (int)(BitLength(numerator) - BitLength(denominator)) - (MantissaBits + 1);
        BigInteger quotient;
        BigInteger remainder;
        BigInteger divisor;
        while (true)
        {
            binaryExponent = Math.Max(binaryExponent, MinExponent);
            divisor = binaryExponent >= 0 ? denominator << binaryExponent : denominator;
            var dividend = binaryExponent >= 0 ? numerator : numerator << -binaryExponent;
            quotient = BigInteger.DivRem(dividend, divisor, out remainder);
            if (quotient >= s_hidden << 1)
            {
                binaryExponent++;
            }
            else if (quotient < s_hidden && binaryExponent > MinExponent)
            {
                binaryExponent--;
            }
            else
            {
                break;
            }
        }

        var cmp = BigInteger.Compare(remainder * 2, divisor);
        if (cmp > 0 || (cmp == 0 && !quotient.IsEven))
        {
            quotient++;
            if (quotient == s_hidden << 1)
            {
                quotient >>= 1;
                binaryExponent++;
            }
        }

        mantissa = (long)quotient;
        exponent = binaryExponent;
        return binaryExponent <= MaxExponent;
    }

    private static long BitLength(BigInteger value)
    {
        var bytes = value.ToByteArray();
        var last = bytes[^1];
        var bits = (bytes.Length - 1) * 8L;
        while (last != 0)
        {
            bits++;
            last >>= 1;
        }

        return bits;
    }
}
