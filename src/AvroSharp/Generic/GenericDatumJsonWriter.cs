using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Writes <see cref="AvroValue"/> data in the Avro JSON encoding for one schema. The schema is compiled once into a
/// tree of typed writer nodes; instances are cached per schema and are thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The encoding follows the specification: a union value other than <c>null</c> is wrapped in an object whose only
/// property names the branch (<c>{"string": "a"}</c>, <c>{"com.example.User": {...}}</c>); <c>bytes</c> and
/// <c>fixed</c> are strings whose code points 0-255 each stand for one byte; enums are their symbol.
/// </para>
/// <para>
/// NaN and infinite <c>float</c> and <c>double</c> values, which JSON cannot express as numbers, are written as the
/// strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>, as Apache.Avro C# writes them.
/// </para>
/// </remarks>
/// <seealso cref="GenericDatumJsonReader"/>
/// <seealso cref="GenericDatumWriter"/>
/// <seealso cref="GenericDatumWriterOptions"/>
public sealed class GenericDatumJsonWriter
{
    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumJsonWriter> s_cache = new();

    private readonly WriterNode _root;

    private GenericDatumJsonWriter(AvroSchema schema, GenericDatumWriterOptions options)
    {
        Schema = schema;
        _root = new Builder(options.MaxDepth).Build(schema);
    }

    /// <summary>Gets the schema values are written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>
    /// Gets the writer for <paramref name="schema"/>, compiling it on first use. Writers with the default options are
    /// cached per schema; writers with custom options are created each time, so keep and reuse them.
    /// </summary>
    /// <param name="schema">The schema to write with.</param>
    /// <param name="options">Limits, or <see langword="null"/> for <see cref="GenericDatumWriterOptions.Default"/>.</param>
    public static GenericDatumJsonWriter Create(AvroSchema schema, GenericDatumWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return options is null || ReferenceEquals(options, GenericDatumWriterOptions.Default)
            ? s_cache.GetValue(schema, static s => new GenericDatumJsonWriter(s, GenericDatumWriterOptions.Default))
            : new GenericDatumJsonWriter(schema, options);
    }

    /// <summary>Writes a value as one JSON value.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    /// <exception cref="AvroException">The value does not match the schema.</exception>
    public void Write(Utf8JsonWriter writer, in AvroValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            _root.Write(writer, value, 0);
        }
        catch (AvroException ex) when (ErrorPath.Get(ex) is { } path)
        {
            throw new AvroException(ErrorPath.DescribeFields(path) + ex.Message, ex);
        }
    }

    /// <summary>Writes a value to a UTF-8 JSON byte array.</summary>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    /// <param name="indented">Whether to indent the output.</param>
    public byte[] WriteToUtf8Bytes(in AvroValue value, bool indented = false)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented }))
        {
            Write(writer, value);
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>Writes a value to a JSON string.</summary>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    /// <param name="indented">Whether to indent the output.</param>
    public string WriteToString(in AvroValue value, bool indented = false)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented }))
        {
            Write(writer, value);
        }

        return System.Text.Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static AvroException Mismatch(AvroSchema schema, in AvroValue value) =>
        new($"A {value.Kind} value cannot be written as {schema.CanonicalForm}.");

    private abstract class WriterNode
    {
        // depth: the number of records around this value.
        public abstract void Write(Utf8JsonWriter writer, in AvroValue value, int depth);
    }

    private sealed class Builder(int maxDepth)
    {
        // Schemas compare by reference.
        private readonly Dictionary<RecordSchema, RecordNode> _records = [];

        public WriterNode Build(AvroSchema schema) => schema switch
        {
            RecordSchema record => BuildRecord(record),
            EnumSchema enumSchema => new EnumNode(enumSchema),
            FixedSchema fixedSchema => new FixedNode(fixedSchema),
            ArraySchema array => new ArrayNode(Build(array.Items)),
            MapSchema map => new MapNode(Build(map.Values)),
            UnionSchema union => new UnionNode(union, union.Branches.ConvertAll(Build)),
            _ => new PrimitiveNode(schema),
        };

        private RecordNode BuildRecord(RecordSchema record)
        {
            // Registered before the fields are built, so recursive records refer to the same node.
            if (_records.TryGetValue(record, out var existing))
            {
                return existing;
            }

            var node = new RecordNode(record, maxDepth);
            _records.Add(record, node);
            node.Fields = record.Fields.ConvertAll(f => Build(f.Schema));
            return node;
        }
    }

    private sealed class PrimitiveNode(AvroSchema schema) : WriterNode
    {
        private readonly AvroSchemaType _type = schema.Type;

        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth)
        {
            switch (_type)
            {
                case AvroSchemaType.Null when value.IsNull:
                    writer.WriteNullValue();
                    break;
                case AvroSchemaType.Boolean when value.IsBoolean:
                    writer.WriteBooleanValue(value.Bits != 0);
                    break;
                case AvroSchemaType.Int when value.IsInt:
                case AvroSchemaType.Long when value.IsLong || value.IsInt:
                    writer.WriteNumberValue(value.Bits);
                    break;
                case AvroSchemaType.Float when value.IsFloat:
                    AvroJsonConventions.WriteSingle(writer, value.SingleUnchecked);
                    break;
                case AvroSchemaType.Float when value.IsInt || value.IsLong:
                    AvroJsonConventions.WriteSingle(writer, value.Bits);
                    break;
                case AvroSchemaType.Double when value.IsDouble:
                    AvroJsonConventions.WriteDouble(writer, value.DoubleUnchecked);
                    break;
                case AvroSchemaType.Double when value.IsFloat:
                    AvroJsonConventions.WriteDouble(writer, value.SingleUnchecked);
                    break;
                case AvroSchemaType.Double when value.IsInt || value.IsLong:
                    AvroJsonConventions.WriteDouble(writer, value.Bits);
                    break;
                case AvroSchemaType.Bytes when value.Reference is byte[] bytes:
                    AvroJsonConventions.WriteByteString(writer, bytes);
                    break;
                case AvroSchemaType.String when value.Reference is string text:
                    writer.WriteStringValue(text);
                    break;
                default:
                    throw Mismatch(schema, value);
            }
        }
    }

    private sealed class RecordNode(RecordSchema schema, int maxDepth) : WriterNode
    {
        private readonly JsonEncodedText[] _names = schema.Fields.ConvertAll(f => JsonEncodedText.Encode(f.Name));

        public WriterNode[] Fields { get; set; } = [];

        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth)
        {
            var fields = Fields;
            if (value.Reference is not GenericRecord record
                || !(ReferenceEquals(record.Schema, schema) || record.Schema.Name == schema.Name)
                || record.FieldCount != fields.Length)
            {
                throw Mismatch(schema, value);
            }

            if (depth >= maxDepth)
            {
                throw new AvroException($"Records are nested more than {maxDepth} levels deep (GenericDatumWriterOptions.MaxDepth); a record may contain itself.");
            }

            writer.WriteStartObject();
            var i = 0;
            try
            {
                for (; i < fields.Length; i++)
                {
                    writer.WritePropertyName(_names[i]);
                    fields[i].Write(writer, record.ValueAt(i), depth + 1);
                }
            }
            catch (AvroException ex) when (ErrorPath.Add(ex, schema.FullName + "." + schema.Fields[i].Name))
            {
                // Never reached: the filter only records the field.
                throw;
            }

            writer.WriteEndObject();
        }
    }

    private sealed class EnumNode(EnumSchema schema) : WriterNode
    {
        // The symbol is the value's own, found in the schema it is written as: the binary writer's check.
        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth) =>
            writer.WriteStringValue(schema.Symbols[GenericDatumWriter.EnumOrdinal(schema, value)]);
    }

    private sealed class FixedNode(FixedSchema schema) : WriterNode
    {
        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth) =>
            AvroJsonConventions.WriteByteString(writer, GenericDatumWriter.FixedBytes(schema, value));
    }

    private sealed class ArrayNode(WriterNode items) : WriterNode
    {
        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth)
        {
            if (value.Reference is not IReadOnlyList<AvroValue> list || value.Kind != AvroValueKind.Array)
            {
                throw new AvroException($"A {value.Kind} value cannot be written as an array.");
            }

            writer.WriteStartArray();
            for (var i = 0; i < list.Count; i++)
            {
                items.Write(writer, list[i], depth);
            }

            writer.WriteEndArray();
        }
    }

    private sealed class MapNode(WriterNode values) : WriterNode
    {
        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth)
        {
            if (value.Reference is not IReadOnlyDictionary<string, AvroValue> map)
            {
                throw new AvroException($"A {value.Kind} value cannot be written as a map.");
            }

            writer.WriteStartObject();
            foreach (var entry in map)
            {
                writer.WritePropertyName(entry.Key);
                values.Write(writer, entry.Value, depth);
            }

            writer.WriteEndObject();
        }
    }

    private sealed class UnionNode : WriterNode
    {
        private readonly UnionSchema _schema;
        private readonly WriterNode[] _branches;
        private readonly UnionBranchSelector _selector;
        private readonly JsonEncodedText[] _names;

        public UnionNode(UnionSchema schema, WriterNode[] branches)
        {
            _schema = schema;
            _branches = branches;
            _selector = new UnionBranchSelector(schema);
            _names = schema.Branches.ConvertAll(b => JsonEncodedText.Encode(AvroJsonConventions.BranchName(b)));
        }

        public override void Write(Utf8JsonWriter writer, in AvroValue value, int depth)
        {
            var index = _selector.IndexOf(value);
            if (index < 0)
            {
                throw Mismatch(_schema, value);
            }

            // The null branch is written as a plain null; every other branch is wrapped.
            if (_schema.Branches[index].Type == AvroSchemaType.Null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName(_names[index]);
            _branches[index].Write(writer, value, depth);
            writer.WriteEndObject();
        }
    }
}
