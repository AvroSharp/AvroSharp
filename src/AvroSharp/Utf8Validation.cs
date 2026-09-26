using System;

namespace AvroSharp;

/// <summary>
/// UTF-8 validation for JSON input. <c>Utf8JsonReader</c> checks the JSON structure but not the bytes inside strings,
/// so invalid UTF-8 only surfaces later as an <see cref="InvalidOperationException"/> from <c>GetString</c>.
/// </summary>
internal static class Utf8Validation
{
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
