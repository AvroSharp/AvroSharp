using System;

namespace AvroSharp;

/// <summary>
/// UTF-8 validation for JSON input. <c>Utf8JsonReader</c> checks the JSON structure but not the bytes inside strings,
/// so invalid UTF-8 only surfaces later as an <see cref="InvalidOperationException"/> from <c>GetString</c>.
/// </summary>
internal static class Utf8Validation
{
    /// <summary>
    /// Returns whether JSON text escapes an unpaired surrogate (<c>\uD800</c> to <c>\uDFFF</c> without its pair), which is
    /// valid JSON syntax but not Unicode text: <c>GetString</c> throws <see cref="InvalidOperationException"/> for it, and
    /// a schema holding one could not be written back. Only <c>\u</c> escapes can hold one, since valid UTF-8 has no
    /// surrogates; malformed escapes are left to the JSON parser.
    /// </summary>
    public static bool HasUnpairedSurrogateEscape(ReadOnlySpan<byte> json)
    {
        // A backslash at the very end is skipped with the byte after it, which isn't there: i can pass the end.
        var i = 0;
        while (i < json.Length)
        {
            var next = json.Slice(i).IndexOf((byte)'\\');
            if (next < 0)
            {
                return false;
            }

            i += next;
            if (i + 6 > json.Length || json[i + 1] != (byte)'u' || !TryHex(json.Slice(i + 2, 4), out var unit))
            {
                i += 2;   // another escape, such as \\ or \", or a malformed one
                continue;
            }

            if (unit is >= 0xDC00 and <= 0xDFFF)
            {
                return true;
            }

            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 12 > json.Length || json[i + 6] != (byte)'\\' || json[i + 7] != (byte)'u'
                    || !TryHex(json.Slice(i + 8, 4), out var low) || low is < 0xDC00 or > 0xDFFF)
                {
                    return true;
                }

                i += 12;
                continue;
            }

            i += 6;
        }

        return false;
    }

    private static bool TryHex(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            var nibble = digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
            {
                return false;
            }

            value = (value << 4) | nibble;
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="utf8"/> is well-formed UTF-8 (no overlong forms, surrogates or values above U+10FFFF).</summary>
    public static bool IsValid(ReadOnlySpan<byte> utf8)
    {
#if NET8_0_OR_GREATER
        return System.Text.Unicode.Utf8.IsValid(utf8);
#else
        var i = 0;
        while (i < utf8.Length)
        {
            var b = utf8[i];
            if (b < 0x80)
            {
                i++;
                continue;
            }

            var length = SequenceLength(b, out var low, out var high);
            if (length == 0 || utf8.Length - i < length || utf8[i + 1] < low || utf8[i + 1] > high)
            {
                return false;
            }

            for (var k = 2; k < length; k++)
            {
                if ((utf8[i + k] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            i += length;
        }

        return true;
#endif
    }

#if !NET8_0_OR_GREATER
    /// <summary>
    /// Gets the length of the sequence a lead byte starts (0 when it cannot start one) and the allowed range of the
    /// second byte, which excludes overlong forms, surrogates and values above U+10FFFF (Unicode Table 3-7).
    /// </summary>
    private static int SequenceLength(byte lead, out byte low, out byte high)
    {
        low = 0x80;
        high = 0xBF;
        switch (lead)
        {
            case >= 0xC2 and <= 0xDF:
                return 2;
            case 0xE0:
                low = 0xA0;
                return 3;
            case 0xED:
                high = 0x9F;
                return 3;
            case >= 0xE1 and <= 0xEF:
                return 3;
            case 0xF0:
                low = 0x90;
                return 4;
            case 0xF4:
                high = 0x8F;
                return 4;
            case >= 0xF1 and <= 0xF3:
                return 4;
            default:
                return 0;
        }
    }
#endif
}
