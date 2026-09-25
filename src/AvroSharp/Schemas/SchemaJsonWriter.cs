using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AvroSharp.Buffers;

namespace AvroSharp.Schemas;

/// <summary>
/// Writes the full JSON form of a schema: every attribute is kept, and each named type is defined at its first
/// occurrence and referred to by name afterwards.
/// </summary>
internal sealed class SchemaJsonWriter
{
    private static readonly JsonWriterOptions s_compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };
    private static readonly JsonWriterOptions s_indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true, Indented = true };

    private readonly Utf8JsonWriter _writer;
    private readonly HashSet<NamedSchema> _defined = [];

    private SchemaJsonWriter(Utf8JsonWriter writer)
    {
        _writer = writer;
    }

    public static string ToJson(AvroSchema schema, bool indented)
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer, indented ? s_indented : s_compact))
        {
            new SchemaJsonWriter(writer).WriteSchema(schema, enclosingNamespace: null);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(AvroSchema schema, Utf8JsonWriter writer) =>
        new SchemaJsonWriter(writer).WriteSchema(schema, enclosingNamespace: null);

    private void WriteSchema(AvroSchema schema, string? enclosingNamespace)
    {
        switch (schema)
        {
            case PrimitiveSchema primitive:
                WritePrimitive(primitive);
                break;
            case NamedSchema named when !_defined.Add(named):
                // Already defined: refer to it by the shortest name that resolves to it.
                _writer.WriteStringValue(string.Equals(named.Name.Namespace, enclosingNamespace, System.StringComparison.Ordinal) ? named.Name.Name : named.FullName);
                break;
            case RecordSchema record:
                WriteRecord(record, enclosingNamespace);
                break;
            case EnumSchema enumSchema:
                WriteEnum(enumSchema, enclosingNamespace);
                break;
            case FixedSchema fixedSchema:
                WriteFixed(fixedSchema, enclosingNamespace);
                break;
            case ArraySchema array:
                _writer.WriteStartObject();
                _writer.WriteString("type"u8, "array"u8);
                _writer.WritePropertyName("items"u8);
                WriteSchema(array.Items, enclosingNamespace);
                WriteProperties(array.Properties);
                _writer.WriteEndObject();
                break;
            case MapSchema map:
                _writer.WriteStartObject();
                _writer.WriteString("type"u8, "map"u8);
                _writer.WritePropertyName("values"u8);
                WriteSchema(map.Values, enclosingNamespace);
                WriteProperties(map.Properties);
                _writer.WriteEndObject();
                break;
            case UnionSchema union:
                _writer.WriteStartArray();
                foreach (var branch in union.Branches)
                {
                    WriteSchema(branch, enclosingNamespace);
                }

                _writer.WriteEndArray();
                break;
            default:
                throw new AvroSchemaException($"Unsupported schema type '{schema.GetType()}'.");
        }
    }

    private void WritePrimitive(PrimitiveSchema schema)
    {
        if (schema.LogicalType is null && schema.Properties.Count == 0)
        {
            _writer.WriteStringValue(schema.TypeName);
            return;
        }

        _writer.WriteStartObject();
        _writer.WriteString("type"u8, schema.TypeName);
        WriteLogicalType(schema.LogicalType);
        WriteProperties(schema.Properties);
        _writer.WriteEndObject();
    }

    private void WriteRecord(RecordSchema record, string? enclosingNamespace)
    {
        _writer.WriteStartObject();
        _writer.WriteString("type"u8, record.IsError ? "error"u8 : "record"u8);
        WriteNameAttributes(record, enclosingNamespace);
        _writer.WritePropertyName("fields"u8);
        _writer.WriteStartArray();
        foreach (var field in record.Fields)
        {
            _writer.WriteStartObject();
            _writer.WriteString("name"u8, field.Name);
            _writer.WritePropertyName("type"u8);
            WriteSchema(field.Schema, record.Name.Namespace);
            if (field.Doc is not null)
            {
                _writer.WriteString("doc"u8, field.Doc);
            }

            if (field.DefaultValue is { } defaultValue)
            {
                _writer.WritePropertyName("default"u8);
                defaultValue.WriteTo(_writer);
            }

            if (field.Order != FieldOrder.Ascending)
            {
                _writer.WriteString("order"u8, field.Order == FieldOrder.Descending ? "descending"u8 : "ignore"u8);
            }

            if (field.Aliases.Count > 0)
            {
                _writer.WritePropertyName("aliases"u8);
                _writer.WriteStartArray();
                foreach (var alias in field.Aliases)
                {
                    _writer.WriteStringValue(alias);
                }

                _writer.WriteEndArray();
            }

            WriteProperties(field.Properties);
            _writer.WriteEndObject();
        }

        _writer.WriteEndArray();
        WriteProperties(record.Properties);
        _writer.WriteEndObject();
    }

    private void WriteEnum(EnumSchema schema, string? enclosingNamespace)
    {
        _writer.WriteStartObject();
        _writer.WriteString("type"u8, "enum"u8);
        WriteNameAttributes(schema, enclosingNamespace);
        _writer.WritePropertyName("symbols"u8);
        _writer.WriteStartArray();
        foreach (var symbol in schema.Symbols)
        {
            _writer.WriteStringValue(symbol);
        }

        _writer.WriteEndArray();
        if (schema.Default is not null)
        {
            _writer.WriteString("default"u8, schema.Default);
        }

        WriteProperties(schema.Properties);
        _writer.WriteEndObject();
    }

    private void WriteFixed(FixedSchema schema, string? enclosingNamespace)
    {
        _writer.WriteStartObject();
        _writer.WriteString("type"u8, "fixed"u8);
        WriteNameAttributes(schema, enclosingNamespace);
        _writer.WriteNumber("size"u8, schema.Size);
        WriteLogicalType(schema.LogicalType);
        WriteProperties(schema.Properties);
        _writer.WriteEndObject();
    }

    private void WriteNameAttributes(NamedSchema schema, string? enclosingNamespace)
    {
        _writer.WriteString("name"u8, schema.Name.Name);
        if (!string.Equals(schema.Name.Namespace, enclosingNamespace, System.StringComparison.Ordinal))
        {
            _writer.WriteString("namespace"u8, schema.Name.Namespace ?? string.Empty);
        }

        if (schema.Doc is not null)
        {
            _writer.WriteString("doc"u8, schema.Doc);
        }

        if (schema.Aliases.Count > 0)
        {
            _writer.WritePropertyName("aliases"u8);
            _writer.WriteStartArray();
            foreach (var alias in schema.Aliases)
            {
                _writer.WriteStringValue(string.Equals(alias.Namespace, schema.Name.Namespace, System.StringComparison.Ordinal) ? alias.Name : alias.FullName);
            }

            _writer.WriteEndArray();
        }
    }

    private void WriteLogicalType(AvroLogicalType? logicalType)
    {
        if (logicalType is null)
        {
            return;
        }

        _writer.WriteString("logicalType"u8, logicalType.Name);
        if (logicalType is DecimalLogicalType decimalType)
        {
            _writer.WriteNumber("precision"u8, decimalType.Precision);
            _writer.WriteNumber("scale"u8, decimalType.Scale);
        }
    }

    private void WriteProperties(IReadOnlyDictionary<string, JsonElement> properties)
    {
        foreach (var property in properties)
        {
            _writer.WritePropertyName(property.Key);
            property.Value.WriteTo(_writer);
        }
    }
}
