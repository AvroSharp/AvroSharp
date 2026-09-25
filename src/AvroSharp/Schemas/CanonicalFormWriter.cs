using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;
using AvroSharp.Buffers;

namespace AvroSharp.Schemas;

/// <summary>
/// Writes the Parsing Canonical Form directly as UTF-8. The transformations of the specification
/// ([PRIMITIVES], [FULLNAMES], [STRIP], [ORDER], [STRINGS], [INTEGERS], [WHITESPACE]) are applied by construction:
/// only the parsing attributes are written, in the prescribed order, with full names, no whitespace and
/// minimal string escaping.
/// </summary>
internal sealed class CanonicalFormWriter
{
    private readonly PooledBufferWriter _output;

    // Schemas use reference equality, so this tracks schema instances.
    private readonly HashSet<NamedSchema> _defined = [];

    private CanonicalFormWriter(PooledBufferWriter output)
    {
        _output = output;
    }

    public static byte[] Write(AvroSchema schema)
    {
        using var output = new PooledBufferWriter();
        new CanonicalFormWriter(output).WriteSchema(schema);
        return output.ToArray();
    }

    private void WriteSchema(AvroSchema schema)
    {
        switch (schema)
        {
            case PrimitiveSchema primitive:
                WriteString(primitive.TypeName);
                break;
            case NamedSchema named when !_defined.Add(named):
                WriteString(named.FullName);
                break;
            case RecordSchema record:
                WriteRecord(record);
                break;
            case EnumSchema enumSchema:
                WriteNamedStart(enumSchema, "enum"u8);
                _output.Write(",\"symbols\":["u8);
                for (var i = 0; i < enumSchema.Symbols.Count; i++)
                {
                    WriteSeparator(i);
                    WriteString(enumSchema.Symbols[i]);
                }

                _output.Write("]}"u8);
                break;
            case FixedSchema fixedSchema:
                WriteNamedStart(fixedSchema, "fixed"u8);
                _output.Write(",\"size\":"u8);
                WriteInteger(fixedSchema.Size);
                _output.Write((byte)'}');
                break;
            case ArraySchema array:
                _output.Write("{\"type\":\"array\",\"items\":"u8);
                WriteSchema(array.Items);
                _output.Write((byte)'}');
                break;
            case MapSchema map:
                _output.Write("{\"type\":\"map\",\"values\":"u8);
                WriteSchema(map.Values);
                _output.Write((byte)'}');
                break;
            case UnionSchema union:
                _output.Write((byte)'[');
                for (var i = 0; i < union.Branches.Count; i++)
                {
                    WriteSeparator(i);
                    WriteSchema(union.Branches[i]);
                }

                _output.Write((byte)']');
                break;
            default:
                throw new AvroSchemaException($"Unsupported schema type '{schema.GetType()}'.");
        }
    }

    private void WriteRecord(RecordSchema record)
    {
        WriteNamedStart(record, "record"u8);
        _output.Write(",\"fields\":["u8);
        for (var i = 0; i < record.Fields.Count; i++)
        {
            WriteSeparator(i);
            var field = record.Fields[i];
            _output.Write("{\"name\":"u8);
            WriteString(field.Name);
            _output.Write(",\"type\":"u8);
            WriteSchema(field.Schema);
            _output.Write((byte)'}');
        }

        _output.Write("]}"u8);
    }

    private void WriteSeparator(int index)
    {
        if (index > 0)
        {
            _output.Write((byte)',');
        }
    }

    private void WriteNamedStart(NamedSchema schema, ReadOnlySpan<byte> type)
    {
        _output.Write("{\"name\":"u8);
        WriteString(schema.FullName);
        _output.Write(",\"type\":\""u8);
        _output.Write(type);
        _output.Write((byte)'"');
    }

    private void WriteInteger(int value)
    {
        var span = _output.GetSpan(11);
        Utf8Formatter.TryFormat(value, span, out var written);
        _output.Advance(written);
    }

    /// <summary>Writes a JSON string, escaping only what JSON requires ([STRINGS]: no \u escapes for other characters).</summary>
    private void WriteString(string value)
    {
        _output.Write((byte)'"');
        var span = _output.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        var length = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        var encoded = span[..length];

        if (!NeedsEscaping(encoded))
        {
            _output.Advance(length);
        }
        else
        {
            // Rare: names are identifiers unless name validation was disabled. Copy out, then escape.
            WriteEscaped(encoded.ToArray());
        }

        _output.Write((byte)'"');
    }

    private static bool NeedsEscaping(ReadOnlySpan<byte> utf8)
    {
#if NET8_0_OR_GREATER
        return utf8.IndexOfAnyInRange((byte)0, (byte)0x1F) >= 0 || utf8.IndexOfAny((byte)'"', (byte)'\\') >= 0;
#else
        foreach (var b in utf8)
        {
            if (b < 0x20 || b == (byte)'"' || b == (byte)'\\')
            {
                return true;
            }
        }

        return false;
#endif
    }

    private void WriteEscaped(byte[] utf8)
    {
        foreach (var b in utf8)
        {
            switch (b)
            {
                case (byte)'"':
                    _output.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    _output.Write("\\\\"u8);
                    break;
                case < 0x20:
                    _output.Write("\\u00"u8);
                    _output.Write((byte)"0123456789abcdef"[b >> 4]);
                    _output.Write((byte)"0123456789abcdef"[b & 0xF]);
                    break;
                default:
                    _output.Write(b);
                    break;
            }
        }
    }
}
