using System;
using System.Runtime.CompilerServices;

namespace AvroSharp.Generic;

/// <summary>The comparisons of the specification's sort order that both comparers share.</summary>
internal static class SortOrder
{
    public const string MapsMessage = "Maps have no sort order in the Avro specification; give the field \"order\": \"ignore\" to leave it out of comparisons.";

    /// <summary>
    /// Compares strings as their UTF-8 bytes, as the specification says, without encoding them: UTF-8 orders by code
    /// point, which UTF-16 code units also do except that surrogates (U+D800-U+DFFF, the halves of a code point above
    /// U+FFFF) must sort after U+E000-U+FFFF. <see cref="string.CompareOrdinal(string, string)"/> would sort them before.
    /// </summary>
    public static int CompareUtf8(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            int a = x[i];
            int b = y[i];
            if (a != b)
            {
                return CodePointOrder(a) < CodePointOrder(b) ? -1 : 1;
            }
        }

        return x.Length.CompareTo(y.Length) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    /// <summary>Compares bytes as unsigned values, then by length.</summary>
    public static int CompareBytes(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) => Math.Sign(x.SequenceCompareTo(y));

    /// <summary>As Java's <c>Float.compare</c>: NaN is the largest value and equals itself, and -0 is less than 0.</summary>
    public static int CompareSingle(float x, float y)
    {
        if (x < y)
        {
            return -1;
        }

        if (x > y)
        {
            return 1;
        }

        var a = float.IsNaN(x) ? 0x7FC00000 : SingleBits(x);
        var b = float.IsNaN(y) ? 0x7FC00000 : SingleBits(y);
        return a.CompareTo(b) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    /// <summary>As Java's <c>Double.compare</c>: NaN is the largest value and equals itself, and -0 is less than 0.</summary>
    public static int CompareDouble(double x, double y)
    {
        if (x < y)
        {
            return -1;
        }

        if (x > y)
        {
            return 1;
        }

        var a = double.IsNaN(x) ? 0x7FF8000000000000 : BitConverter.DoubleToInt64Bits(x);
        var b = double.IsNaN(y) ? 0x7FF8000000000000 : BitConverter.DoubleToInt64Bits(y);
        return a.CompareTo(b) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    // Equal values that differ only in sign are 0 and -0: their bits compare as signed integers, so -0 (negative) is less.
    private static int SingleBits(float value) => Unsafe.As<float, int>(ref value);

    // Surrogates move above U+FFFF's other code units; every other code unit keeps its place.
    private static int CodePointOrder(int c) => c >= 0xD800 ? (c >= 0xE000 ? c - 0x800 : c + 0x2000) : c;
}
