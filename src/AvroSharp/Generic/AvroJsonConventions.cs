using System;
using System.Buffers;
using System.Text.Json;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>The value conventions of the Avro JSON encoding that are not plain JSON.</summary>
internal static class AvroJsonConventions
{
    // Written as strings: JSON has no literal for them. This matches Json.NET's default, which Apache.Avro C# uses.
    public const string NaN = "NaN";
    public const string PositiveInfinity = "Infinity";
    public const string NegativeInfinity = "-Infinity";

    private const int StackallocLimit = 256;

    /// <summary>Gets the name that identifies a union branch: the full name of a named type, otherwise the type name.</summary>
    public static string BranchName(AvroSchema branch) =>
        branch is NamedSchema named ? named.FullName : AvroNames.GetTypeName(branch.Type);

    /// <summary>Writes bytes as a string whose code points 0-255 each stand for one byte (ISO-8859-1).</summary>
    public static void WriteByteString(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        char[]? rented = null;
        Span<char> chars = bytes.Length <= StackallocLimit
            ? stackalloc char[StackallocLimit]
            : (rented = ArrayPool<char>.Shared.Rent(bytes.Length));
        chars = chars[..bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        writer.WriteStringValue(chars);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Converts a byte string back to bytes; <see langword="null"/> when a code point is above 255.</summary>
    public static byte[]? ParseByteString(string value)
    {
        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c > 0xFF)
            {
                return null;
            }

            bytes[i] = (byte)c;
        }

        return bytes;
    }

    public static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value))
        {
            writer.WriteStringValue(NaN);
        }
        else if (double.IsInfinity(value))
        {
            writer.WriteStringValue(value > 0 ? PositiveInfinity : NegativeInfinity);
        }
        else
        {
            writer.WriteNumberValue(value);
        }
    }

    public static void WriteSingle(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value))
        {
            writer.WriteStringValue(NaN);
        }
        else if (float.IsInfinity(value))
        {
            writer.WriteStringValue(value > 0 ? PositiveInfinity : NegativeInfinity);
        }
        else
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>Reads one of the three non-finite literals.</summary>
    public static bool TryParseNonFinite(string? value, out double result)
    {
        switch (value)
        {
            case NaN:
                result = double.NaN;
                return true;
            case PositiveInfinity:
                result = double.PositiveInfinity;
                return true;
            case NegativeInfinity:
                result = double.NegativeInfinity;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
