using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>Where <see cref="SchemaJsonWriter"/> writes: a <see cref="Utf8JsonWriter"/>, or Jackson-style compact text.</summary>
internal interface IJsonOutput
{
    void StartObject();

    void EndObject();

    void StartArray();

    void EndArray();

    void Name(string name);

    void String(string value);

    void Number(int value);

    /// <summary>Writes a default value or property as Jackson (and so Avro Java) prints it after parsing.</summary>
    void Value(JsonElement value);
}

/// <summary>Writes through a <see cref="Utf8JsonWriter"/>, which does its own escaping and indentation.</summary>
internal sealed class Utf8JsonOutput(Utf8JsonWriter writer) : IJsonOutput
{
    public void StartObject() => writer.WriteStartObject();

    public void EndObject() => writer.WriteEndObject();

    public void StartArray() => writer.WriteStartArray();

    public void EndArray() => writer.WriteEndArray();

    public void Name(string name) => writer.WritePropertyName(name);

    public void String(string value) => writer.WriteStringValue(value);

    public void Number(int value) => writer.WriteNumberValue(value);

    public void Value(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Value(property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Value(item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(JavaJsonNumbers.Format(value.GetRawText()), skipInputValidation: true);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// Compact JSON text as Jackson writes it by default: no whitespace, only <c>"</c>, <c>\</c> and control characters
/// escaped (with the short forms <c>\b \t \n \f \r</c> and otherwise <c>\u00XX</c> in upper case), everything else,
/// including non-ASCII, written as is.
/// </summary>
internal sealed class JacksonJsonOutput : IJsonOutput
{
    private const string Hex = "0123456789ABCDEF";

    private readonly StringBuilder _text = new(256);

    // One entry per open object or array: whether the next value needs a comma before it.
    private readonly Stack<bool> _needsComma = new();
    private bool _afterName;

    public void StartObject()
    {
        BeforeValue();
        _text.Append('{');
        _needsComma.Push(false);
    }

    public void EndObject()
    {
        _needsComma.Pop();
        _text.Append('}');
    }

    public void StartArray()
    {
        BeforeValue();
        _text.Append('[');
        _needsComma.Push(false);
    }

    public void EndArray()
    {
        _needsComma.Pop();
        _text.Append(']');
    }

    public void Name(string name)
    {
        BeforeValue();
        AppendQuoted(name);
        _text.Append(':');
        _afterName = true;
    }

    public void String(string value)
    {
        BeforeValue();
        AppendQuoted(value);
    }

    public void Number(int value)
    {
        BeforeValue();
        _text.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Value(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                StartObject();
                foreach (var property in value.EnumerateObject())
                {
                    Name(property.Name);
                    Value(property.Value);
                }

                EndObject();
                break;
            case JsonValueKind.Array:
                StartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Value(item);
                }

                EndArray();
                break;
            case JsonValueKind.String:
                String(value.GetString()!);
                break;
            case JsonValueKind.Number:
                BeforeValue();
                _text.Append(JavaJsonNumbers.Format(value.GetRawText()));
                break;
            case JsonValueKind.True:
                BeforeValue();
                _text.Append("true");
                break;
            case JsonValueKind.False:
                BeforeValue();
                _text.Append("false");
                break;
            default:
                BeforeValue();
                _text.Append("null");
                break;
        }
    }

    public override string ToString() => _text.ToString();

    // A value after a name follows the colon; any other value in a container follows a comma unless it is the first.
    private void BeforeValue()
    {
        if (_afterName)
        {
            _afterName = false;
            return;
        }

        if (_needsComma.Count > 0)
        {
            if (_needsComma.Pop())
            {
                _text.Append(',');
            }

            _needsComma.Push(true);
        }
    }

    private void AppendQuoted(string value)
    {
        _text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    _text.Append("\\\"");
                    break;
                case '\\':
                    _text.Append("\\\\");
                    break;
                case '\b':
                    _text.Append("\\b");
                    break;
                case '\t':
                    _text.Append("\\t");
                    break;
                case '\n':
                    _text.Append("\\n");
                    break;
                case '\f':
                    _text.Append("\\f");
                    break;
                case '\r':
                    _text.Append("\\r");
                    break;
                case < ' ':
                    _text.Append("\\u00").Append(Hex[c >> 4]).Append(Hex[c & 0xF]);
                    break;
                default:
                    _text.Append(c);
                    break;
            }
        }

        _text.Append('"');
    }
}

