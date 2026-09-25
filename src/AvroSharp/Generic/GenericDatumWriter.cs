using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Writes <see cref="AvroValue"/> data in Avro binary encoding for one schema. The schema is compiled once into a
/// tree of typed writer nodes, so writing does not inspect the schema; instances are cached per schema and are
/// thread-safe.
/// </summary>
public sealed class GenericDatumWriter
{
    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumWriter> s_cache = new();

    private readonly WriterNode _root;

    private GenericDatumWriter(AvroSchema schema)
    {
        Schema = schema;
        _root = new Builder().Build(schema);
    }

    /// <summary>Gets the schema values are written with.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the writer for <paramref name="schema"/>, compiling it on first use.</summary>
    /// <param name="schema">The schema to write with.</param>
    public static GenericDatumWriter Create(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return s_cache.GetValue(schema, static s => new GenericDatumWriter(s));
    }

    /// <summary>Writes a value.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    /// <exception cref="AvroException">The value does not match the schema.</exception>
    public void Write(ref AvroWriter writer, in AvroValue value) => _root.Write(ref writer, value);

    /// <summary>Writes a value to <paramref name="output"/> and commits it.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    public void Write(IBufferWriter<byte> output, in AvroValue value)
    {
        var writer = new AvroWriter(output);
        _root.Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Writes a value to a new array.</summary>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    public byte[] WriteToArray(in AvroValue value)
    {
        var output = new ArrayBufferWriter<byte>();
        Write(output, value);
        return output.WrittenSpan.ToArray();
    }

    private static AvroException Mismatch(AvroSchema schema, in AvroValue value) =>
        new($"A {value.Kind} value cannot be written as {schema.CanonicalForm}.");

    private abstract class WriterNode
    {
        public abstract void Write(ref AvroWriter writer, in AvroValue value);
    }

    private sealed class Builder
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
            _ => schema.Type switch
            {
                AvroSchemaType.Null => new NullNode(schema),
                AvroSchemaType.Boolean => new BooleanNode(schema),
                AvroSchemaType.Int => new IntNode(schema),
                AvroSchemaType.Long => new LongNode(schema),
                AvroSchemaType.Float => new FloatNode(schema),
                AvroSchemaType.Double => new DoubleNode(schema),
                AvroSchemaType.Bytes => new BytesNode(schema),
                _ => new StringNode(schema),
            },
        };

        private RecordNode BuildRecord(RecordSchema record)
        {
            // Registered before the fields are built, so recursive records refer to the same node.
            if (_records.TryGetValue(record, out var existing))
            {
                return existing;
            }

            var node = new RecordNode(record);
            _records.Add(record, node);
            node.Fields = record.Fields.ConvertAll(f => Build(f.Schema));
            return node;
        }
    }

    private sealed class NullNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value)
        {
            if (!value.IsNull)
            {
                throw Mismatch(schema, value);
            }
        }
    }

    private sealed class BooleanNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteBoolean(value.Kind == AvroValueKind.Boolean ? value.AsBoolean() : throw Mismatch(schema, value));
    }

    private sealed class IntNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteInt(value.Kind == AvroValueKind.Int ? (int)value.Bits : throw Mismatch(schema, value));
    }

    private sealed class LongNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteLong(value.Kind is AvroValueKind.Long or AvroValueKind.Int ? value.Bits : throw Mismatch(schema, value));
    }

    private sealed class FloatNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) => writer.WriteFloat(value.Kind switch
        {
            AvroValueKind.Float => value.AsSingle(),
            AvroValueKind.Int or AvroValueKind.Long => value.Bits,
            _ => throw Mismatch(schema, value),
        });
    }

    private sealed class DoubleNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) => writer.WriteDouble(value.Kind switch
        {
            AvroValueKind.Double or AvroValueKind.Float => value.AsDouble(),
            AvroValueKind.Int or AvroValueKind.Long => value.Bits,
            _ => throw Mismatch(schema, value),
        });
    }

    private sealed class BytesNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteBytes(value.Reference as byte[] ?? throw Mismatch(schema, value));
    }

    private sealed class StringNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteString(value.Reference as string ?? throw Mismatch(schema, value));
    }

    private sealed class RecordNode(RecordSchema schema) : WriterNode
    {
        public WriterNode[] Fields { get; set; } = [];

        public override void Write(ref AvroWriter writer, in AvroValue value)
        {
            if (value.Reference is not GenericRecord record || record.Schema.Name != schema.Name || record.Values.Count != Fields.Length)
            {
                throw Mismatch(schema, value);
            }

            var fields = Fields;
            for (var i = 0; i < fields.Length; i++)
            {
                try
                {
                    fields[i].Write(ref writer, record[i]);
                }
                catch (AvroException ex) when (ex is not AvroDataException)
                {
                    throw new AvroException($"Field '{schema.FullName}.{schema.Fields[i].Name}': {ex.Message}", ex);
                }
            }
        }
    }

    private sealed class EnumNode(EnumSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteEnum(value.EnumSchema is { } enumSchema && enumSchema.Name == schema.Name
                ? (int)value.Bits
                : throw Mismatch(schema, value));
    }

    private sealed class FixedNode(FixedSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value) =>
            writer.WriteFixed(value.Reference is GenericFixed fixedValue && fixedValue.Schema.Name == schema.Name
                ? fixedValue.Bytes.Span
                : throw Mismatch(schema, value));
    }

    private sealed class ArrayNode(WriterNode items) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value)
        {
            var list = value.Kind == AvroValueKind.Array ? value.AsArray() : throw new AvroException($"A {value.Kind} value cannot be written as an array.");
            if (list.Count > 0)
            {
                writer.WriteBlockCount(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    items.Write(ref writer, list[i]);
                }
            }

            writer.WriteBlockEnd();
        }
    }

    private sealed class MapNode(WriterNode values) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value)
        {
            var map = value.Kind == AvroValueKind.Map ? value.AsMap() : throw new AvroException($"A {value.Kind} value cannot be written as a map.");
            if (map.Count > 0)
            {
                writer.WriteBlockCount(map.Count);
                foreach (var entry in map)
                {
                    writer.WriteString(entry.Key);
                    values.Write(ref writer, entry.Value);
                }
            }

            writer.WriteBlockEnd();
        }
    }

    /// <summary>Selects the branch from the value's kind (or, for named types, its schema's full name).</summary>
    private sealed class UnionNode : WriterNode
    {
        private readonly UnionSchema _schema;
        private readonly WriterNode[] _branches;
        private readonly int[] _byKind;
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

        public UnionNode(UnionSchema schema, WriterNode[] branches)
        {
            _schema = schema;
            _branches = branches;
            _byKind = new int[(int)AvroValueKind.Fixed + 1];
            _byKind.AsSpan().Fill(-1);
            for (var i = 0; i < schema.Branches.Count; i++)
            {
                var branch = schema.Branches[i];
                if (branch is NamedSchema named)
                {
                    _byName[named.FullName] = i;
                }
                else
                {
                    _byKind[(int)KindOf(branch.Type)] = i;
                }
            }

            // Widening, used only when the union has no branch of the value's own kind.
            Widen(AvroValueKind.Int, AvroValueKind.Long, AvroValueKind.Float, AvroValueKind.Double);
            Widen(AvroValueKind.Long, AvroValueKind.Float, AvroValueKind.Double);
            Widen(AvroValueKind.Float, AvroValueKind.Double);
        }

        public override void Write(ref AvroWriter writer, in AvroValue value)
        {
            var kind = value.Kind;
            var index = kind switch
            {
                AvroValueKind.Record => IndexOfName(value.AsRecord().Schema.FullName),
                AvroValueKind.Enum => IndexOfName(value.EnumSchema!.FullName),
                AvroValueKind.Fixed => IndexOfName(value.AsFixed().Schema.FullName),
                _ => _byKind[(int)kind],
            };

            if (index < 0)
            {
                throw Mismatch(_schema, value);
            }

            writer.WriteUnionIndex(index);
            _branches[index].Write(ref writer, value);
        }

        private static AvroValueKind KindOf(AvroSchemaType type) => type switch
        {
            AvroSchemaType.Null => AvroValueKind.Null,
            AvroSchemaType.Boolean => AvroValueKind.Boolean,
            AvroSchemaType.Int => AvroValueKind.Int,
            AvroSchemaType.Long => AvroValueKind.Long,
            AvroSchemaType.Float => AvroValueKind.Float,
            AvroSchemaType.Double => AvroValueKind.Double,
            AvroSchemaType.Bytes => AvroValueKind.Bytes,
            AvroSchemaType.String => AvroValueKind.String,
            AvroSchemaType.Array => AvroValueKind.Array,
            _ => AvroValueKind.Map,
        };

        private int IndexOfName(string fullName) => _byName.TryGetValue(fullName, out var index) ? index : -1;

        private void Widen(AvroValueKind from, params AvroValueKind[] targets)
        {
            if (_byKind[(int)from] >= 0)
            {
                return;
            }

            foreach (var target in targets)
            {
                if (_byKind[(int)target] >= 0)
                {
                    _byKind[(int)from] = _byKind[(int)target];
                    return;
                }
            }
        }
    }
}

/// <summary>Array helpers for the internal builders.</summary>
internal static class ListExtensions
{
    public static TResult[] ConvertAll<TSource, TResult>(this IReadOnlyList<TSource> source, Func<TSource, TResult> convert)
    {
        var result = new TResult[source.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = convert(source[i]);
        }

        return result;
    }
}
