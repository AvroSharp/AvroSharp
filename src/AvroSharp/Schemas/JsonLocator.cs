using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>A step in a JSON path: an object property or an array index.</summary>
internal readonly struct JsonPathSegment
{
    private JsonPathSegment(string? property, int index)
    {
        Property = property;
        Index = index;
    }

    public string? Property { get; }

    public int Index { get; }

    public static JsonPathSegment ForProperty(string name) => new(name, -1);

    public static JsonPathSegment ForIndex(int index) => new(null, index);
}

/// <summary>
/// Maps a JSON path back to a line and column in the source text. Used only when reporting an error,
/// so the fast path never tracks positions.
/// </summary>
internal static class JsonLocator
{
    public static string FormatPath(IReadOnlyList<JsonPathSegment> path)
    {
        var builder = new StringBuilder("$");
        foreach (var segment in path)
        {
            if (segment.Property is { } property)
            {
                if (AvroNames.IsValidName(property.AsSpan()))
                {
                    builder.Append('.').Append(property);
                }
                else
                {
                    builder.Append("['");
                    foreach (var ch in property)
                    {
                        if (ch is '\'' or '\\')
                        {
                            builder.Append('\\');
                        }

                        builder.Append(ch);
                    }

                    builder.Append("']");
                }
            }
            else
            {
                builder.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
        }

        return builder.ToString();
    }

    public static bool TryLocate(ReadOnlySpan<byte> json, JsonReaderOptions options, IReadOnlyList<JsonPathSegment> path, out long line, out long column)
    {
        line = 0;
        column = 0;
        try
        {
            var reader = new Utf8JsonReader(json, options);
            if (!reader.Read() || !Seek(ref reader, path, 0))
            {
                return false;
            }

            var offset = (int)reader.TokenStartIndex;
            var before = json[..offset];
            var lastNewline = before.LastIndexOf((byte)'\n');
            line = 1 + Count(before, (byte)'\n');
            column = offset - lastNewline;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Seek(ref Utf8JsonReader reader, IReadOnlyList<JsonPathSegment> path, int depth)
    {
        if (depth == path.Count)
        {
            return true;
        }

        var segment = path[depth];
        if (segment.Property is { } property)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            // The parser reads the last occurrence of a duplicated property, so the locator does too.
            var found = false;
            var snapshot = default(Utf8JsonReader);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var matches = reader.ValueTextEquals(property);
                reader.Read();
                if (matches)
                {
                    snapshot = reader;
                    found = true;
                }

                reader.Skip();
            }

            if (!found)
            {
                return false;
            }

            reader = snapshot;
            return Seek(ref reader, path, depth + 1);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        for (var i = 0; reader.Read() && reader.TokenType != JsonTokenType.EndArray; i++)
        {
            if (i == segment.Index)
            {
                return Seek(ref reader, path, depth + 1);
            }

            reader.Skip();
        }

        return false;
    }

    private static int Count(ReadOnlySpan<byte> span, byte value)
    {
#if NET8_0_OR_GREATER
        return span.Count(value);
#else
        var count = 0;
        foreach (var b in span)
        {
            if (b == value)
            {
                count++;
            }
        }

        return count;
#endif
    }
}
