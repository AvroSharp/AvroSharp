using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>
/// Writes the full JSON form of a schema: every attribute is kept, and each named type is defined at its first
/// occurrence and referred to by name afterwards. Attributes come in the order Apache Avro Java writes them, and
/// numbers in defaults and properties are printed as Java prints them, so the compact form is the text of Java's
/// <c>Schema.toString()</c>, which is what schema registries compare.
/// </summary>
internal sealed class SchemaJsonWriter
{
    private static readonly JsonWriterOptions s_indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true, Indented = true };

    private readonly IJsonOutput _out;
    private readonly HashSet<string> _known = new(System.StringComparer.Ordinal);

    private SchemaJsonWriter(IJsonOutput output, IEnumerable<NamedSchema>? referenced)
    {
        _out = output;
        if (referenced is not null)
        {
            foreach (var named in referenced)
            {
                _known.Add((named ?? throw new System.ArgumentException("A referenced schema is null.", nameof(referenced))).FullName);
            }
        }
    }

    /// <summary>Writes a schema; named types in <paramref name="referenced"/> are written by name, never defined.</summary>
    public static string ToJson(AvroSchema schema, bool indented, IEnumerable<NamedSchema>? referenced = null)
    {
        if (!indented)
        {
            var compact = new JacksonJsonOutput();
            new SchemaJsonWriter(compact, referenced).WriteSchema(schema, enclosingNamespace: null);
            return compact.ToString();
        }

        using var buffer = new Buffers.PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer, s_indented))
        {
            new SchemaJsonWriter(new Utf8JsonOutput(writer), referenced).WriteSchema(schema, enclosingNamespace: null);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(AvroSchema schema, Utf8JsonWriter writer) =>
        new SchemaJsonWriter(new Utf8JsonOutput(writer), referenced: null).WriteSchema(schema, enclosingNamespace: null);

    private void WriteSchema(AvroSchema schema, string? enclosingNamespace)
    {
        switch (schema)
        {
            case PrimitiveSchema primitive:
                WritePrimitive(primitive);
                break;
            case NamedSchema named when !_known.Add(named.FullName):
                // Already defined, or referenced: refer to it by the shortest name that resolves to it.
                _out.String(string.Equals(named.Name.Namespace, enclosingNamespace, System.StringComparison.Ordinal) ? named.Name.Name : named.FullName);
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
                _out.StartObject();
                _out.Name("type");
                _out.String("array");
                _out.Name("items");
                WriteSchema(array.Items, enclosingNamespace);
                WriteProperties(array.Properties);
                _out.EndObject();
                break;
            case MapSchema map:
                _out.StartObject();
                _out.Name("type");
                _out.String("map");
                _out.Name("values");
                WriteSchema(map.Values, enclosingNamespace);
                WriteProperties(map.Properties);
                _out.EndObject();
                break;
            case UnionSchema union:
                _out.StartArray();
                foreach (var branch in union.Branches)
                {
                    WriteSchema(branch, enclosingNamespace);
                }

                _out.EndArray();
                break;
            default:
                throw new AvroSchemaException($"Unsupported schema type '{schema.GetType()}'.");
        }
    }

    private void WritePrimitive(PrimitiveSchema schema)
    {
        if (schema.LogicalType is null && schema.Properties.Count == 0)
        {
            _out.String(schema.TypeName);
            return;
        }

        _out.StartObject();
        _out.Name("type");
        _out.String(schema.TypeName);
        WriteLogicalType(schema.LogicalType);
        WriteProperties(schema.Properties);
        _out.EndObject();
    }

    private void WriteRecord(RecordSchema record, string? enclosingNamespace)
    {
        _out.StartObject();
        _out.Name("type");
        _out.String(record.IsError ? "error" : "record");
        WriteNameAndDoc(record, enclosingNamespace);
        _out.Name("fields");
        _out.StartArray();
        foreach (var field in record.Fields)
        {
            WriteField(field, record.Name.Namespace);
        }

        _out.EndArray();
        WriteProperties(record.Properties);
        WriteAliases(record);
        _out.EndObject();
    }

    private void WriteField(RecordField field, string? recordNamespace)
    {
        _out.StartObject();
        _out.Name("name");
        _out.String(field.Name);
        _out.Name("type");
        WriteSchema(field.Schema, recordNamespace);
        if (field.Doc is not null)
        {
            _out.Name("doc");
            _out.String(field.Doc);
        }

        if (field.DefaultValue is { } defaultValue)
        {
            _out.Name("default");
            _out.Value(defaultValue);
        }

        if (field.Order != FieldOrder.Ascending)
        {
            _out.Name("order");
            _out.String(field.Order == FieldOrder.Descending ? "descending" : "ignore");
        }

        if (field.Aliases.Count > 0)
        {
            _out.Name("aliases");
            _out.StartArray();
            foreach (var alias in field.Aliases)
            {
                _out.String(alias);
            }

            _out.EndArray();
        }

        WriteProperties(field.Properties);
        _out.EndObject();
    }

    private void WriteEnum(EnumSchema schema, string? enclosingNamespace)
    {
        _out.StartObject();
        _out.Name("type");
        _out.String("enum");
        WriteNameAndDoc(schema, enclosingNamespace);
        _out.Name("symbols");
        _out.StartArray();
        foreach (var symbol in schema.Symbols)
        {
            _out.String(symbol);
        }

        _out.EndArray();
        if (schema.DefaultSymbol is not null)
        {
            _out.Name("default");
            _out.String(schema.DefaultSymbol);
        }

        WriteProperties(schema.Properties);
        WriteAliases(schema);
        _out.EndObject();
    }

    private void WriteFixed(FixedSchema schema, string? enclosingNamespace)
    {
        _out.StartObject();
        _out.Name("type");
        _out.String("fixed");
        WriteNameAndDoc(schema, enclosingNamespace);
        _out.Name("size");
        _out.Number(schema.Size);
        WriteLogicalType(schema.LogicalType);
        WriteProperties(schema.Properties);
        WriteAliases(schema);
        _out.EndObject();
    }

    // As Java: a namespace is written when it differs from the enclosing one, and "" when a type without one is
    // nested in a namespace.
    private void WriteNameAndDoc(NamedSchema schema, string? enclosingNamespace)
    {
        _out.Name("name");
        _out.String(schema.Name.Name);
        if (!string.Equals(schema.Name.Namespace, enclosingNamespace, System.StringComparison.Ordinal))
        {
            _out.Name("namespace");
            _out.String(schema.Name.Namespace ?? string.Empty);
        }

        if (schema.Doc is not null)
        {
            _out.Name("doc");
            _out.String(schema.Doc);
        }
    }

    // Java writes a named type's aliases last, after its properties.
    private void WriteAliases(NamedSchema schema)
    {
        if (schema.Aliases.Count == 0)
        {
            return;
        }

        _out.Name("aliases");
        _out.StartArray();
        foreach (var alias in schema.Aliases)
        {
            _out.String(string.Equals(alias.Namespace, schema.Name.Namespace, System.StringComparison.Ordinal) ? alias.Name : alias.FullName);
        }

        _out.EndArray();
    }

    private void WriteLogicalType(AvroLogicalType? logicalType)
    {
        if (logicalType is null)
        {
            return;
        }

        _out.Name("logicalType");
        _out.String(logicalType.Name);
        if (logicalType is DecimalLogicalType decimalType)
        {
            _out.Name("precision");
            _out.Number(decimalType.Precision);
            _out.Name("scale");
            _out.Number(decimalType.Scale);
        }
    }

    private void WriteProperties(IReadOnlyDictionary<string, JsonElement> properties)
    {
        foreach (var property in properties)
        {
            _out.Name(property.Key);
            _out.Value(property.Value);
        }
    }
}
