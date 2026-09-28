using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif
using AvroSharp.Buffers;
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

    private GenericDatumWriter(AvroSchema schema, GenericDatumWriterOptions options)
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
    public static GenericDatumWriter Create(AvroSchema schema, GenericDatumWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return options is null || ReferenceEquals(options, GenericDatumWriterOptions.Default)
            ? s_cache.GetValue(schema, static s => new GenericDatumWriter(s, GenericDatumWriterOptions.Default))
            : new GenericDatumWriter(schema, options);
    }

    /// <summary>Writes a value.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    /// <exception cref="AvroException">The value does not match the schema.</exception>
    public void Write(ref AvroWriter writer, in AvroValue value)
    {
        try
        {
            _root.Write(ref writer, value, 0);
        }
        catch (AvroException ex) when (ex is not AvroDataException && ErrorPath.Get(ex) is { } path)
        {
            // Record nodes add their field to the path from exception filters (see ErrorPath).
            throw new AvroException(ErrorPath.DescribeFields(path) + ex.Message, ex);
        }
    }

    /// <summary>Writes a value to <paramref name="output"/> and commits it.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    public void Write(IBufferWriter<byte> output, in AvroValue value)
    {
        var writer = new AvroWriter(output);
        Write(ref writer, value);
        writer.Flush();
    }

    /// <summary>Writes a value to a new array.</summary>
    /// <param name="value">A value matching <see cref="Schema"/>.</param>
    public byte[] WriteToArray(in AvroValue value)
    {
        using var output = new PooledBufferWriter();
        Write(output, value);
        return output.ToArray();
    }

    private static AvroException Mismatch(AvroSchema schema, in AvroValue value) =>
        new($"A {value.Kind} value cannot be written as {schema.CanonicalForm}.");

    /// <summary>Exception filter: adds a field to the path and returns false, so the exception keeps propagating.</summary>
    private static bool AddToPath(AvroException ex, RecordSchema schema, int field) =>
        ex is not AvroDataException && ErrorPath.Add(ex, schema.FullName + "." + schema.Fields[field].Name);

    private abstract class WriterNode
    {
        // depth: the number of records around this value.
        public abstract void Write(ref AvroWriter writer, in AvroValue value, int depth);
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

            var node = new RecordNode(record, maxDepth);
            _records.Add(record, node);
            node.Fields = record.Fields.ConvertAll(f => Build(f.Schema));
            return node;
        }
    }

    private sealed class NullNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth)
        {
            if (!value.IsNull)
            {
                throw Mismatch(schema, value);
            }
        }
    }

    private sealed class BooleanNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteBoolean(value.IsBoolean ? value.Bits != 0 : throw Mismatch(schema, value));
    }

    private sealed class IntNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteInt(value.IsInt ? (int)value.Bits : throw Mismatch(schema, value));
    }

    private sealed class LongNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteLong(value.IsLong || value.IsInt ? value.Bits : throw Mismatch(schema, value));
    }

    private sealed class FloatNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) => writer.WriteFloat(
            value.IsFloat ? value.SingleUnchecked
            : value.IsInt || value.IsLong ? value.Bits
            : throw Mismatch(schema, value));
    }

    private sealed class DoubleNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) => writer.WriteDouble(
            value.IsDouble ? value.DoubleUnchecked
            : value.IsFloat ? value.SingleUnchecked
            : value.IsInt || value.IsLong ? value.Bits
            : throw Mismatch(schema, value));
    }

    private sealed class BytesNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteBytes(value.Reference as byte[] ?? throw Mismatch(schema, value));
    }

    private sealed class StringNode(AvroSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteString(value.Reference as string ?? throw Mismatch(schema, value));
    }

    private sealed class RecordNode(RecordSchema schema, int maxDepth) : WriterNode
    {
        public WriterNode[] Fields { get; set; } = [];

        public override void Write(ref AvroWriter writer, in AvroValue value, int depth)
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

            var i = 0;
            try
            {
                for (; i < fields.Length; i++)
                {
                    fields[i].Write(ref writer, record.ValueAt(i), depth + 1);
                }
            }
            catch (AvroException ex) when (AddToPath(ex, schema, i))
            {
                // Never reached: the filter only records the field.
                throw;
            }
        }
    }

    private sealed class EnumNode(EnumSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteEnum(value.EnumSchema is { } enumSchema && enumSchema.Name == schema.Name
                ? (int)value.Bits
                : throw Mismatch(schema, value));
    }

    private sealed class FixedNode(FixedSchema schema) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth) =>
            writer.WriteFixed(value.Reference is GenericFixed fixedValue && fixedValue.Schema.Name == schema.Name
                ? fixedValue.Bytes.Span
                : throw Mismatch(schema, value));
    }

    private sealed class ArrayNode(WriterNode items) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth)
        {
            // The concrete types the reader creates are written from a span, without interface calls.
            switch (value.Reference)
            {
                case PrimitiveArray primitives when TryWritePrimitives(ref writer, primitives):
                    break;
#if NET8_0_OR_GREATER
                case List<AvroValue> list:
                    WriteItems(ref writer, CollectionsMarshal.AsSpan(list), depth);
                    break;
#endif
                case AvroValue[] array:
                    WriteItems(ref writer, array, depth);
                    break;
                case IReadOnlyList<AvroValue> list:
                    if (list.Count > 0)
                    {
                        writer.WriteBlockCount(list.Count);
                        for (var i = 0; i < list.Count; i++)
                        {
                            items.Write(ref writer, list[i], depth);
                        }
                    }

                    writer.WriteBlockEnd();
                    break;
                default:
                    throw new AvroException($"A {value.Kind} value cannot be written as an array.");
            }
        }

        // An array stored as primitives is written from its memory when the items' schema takes them as they are:
        // booleans, doubles and floats as one copy, ints and longs without a kind check per item. Any other
        // combination (for example ints written as doubles) goes item by item through the items' node.
        private bool TryWritePrimitives(ref AvroWriter writer, PrimitiveArray array)
        {
            switch (array, items)
            {
                case (_, _) when array.Count == 0:
                    break;
                case (Int64Array longs, LongNode):
                    writer.WriteBlockCount(longs.Count);
                    writer.WriteLongs(longs.Items.Span);

                    break;
                case (Int32Array ints, IntNode or LongNode):
                    // An int and a long with the same value have the same encoding.
                    writer.WriteBlockCount(ints.Count);
                    writer.WriteInts(ints.Items.Span);

                    break;
                case (DoubleArray doubles, DoubleNode):
                    writer.WriteBlockCount(doubles.Count);
                    writer.WriteDoubles(doubles.Items.Span);
                    break;
                case (SingleArray floats, FloatNode):
                    writer.WriteBlockCount(floats.Count);
                    writer.WriteFloats(floats.Items.Span);
                    break;
                case (BooleanArray booleans, BooleanNode):
                    writer.WriteBlockCount(booleans.Count);
                    writer.WriteBooleans(booleans.Items.Span);
                    break;
                default:
                    return false;
            }

            writer.WriteBlockEnd();
            return true;
        }

        private void WriteItems(ref AvroWriter writer, ReadOnlySpan<AvroValue> values, int depth)
        {
            if (values.Length > 0)
            {
                writer.WriteBlockCount(values.Length);
                foreach (ref readonly var item in values)
                {
                    items.Write(ref writer, item, depth);
                }
            }

            writer.WriteBlockEnd();
        }
    }

    private sealed class MapNode(WriterNode values) : WriterNode
    {
        public override void Write(ref AvroWriter writer, in AvroValue value, int depth)
        {
            if (value.Reference is Dictionary<string, AvroValue> dictionary)
            {
                // The concrete type the reader creates: its struct enumerator avoids a boxed enumerator per write.
                if (dictionary.Count > 0)
                {
                    writer.WriteBlockCount(dictionary.Count);
                    foreach (var entry in dictionary)
                    {
                        writer.WriteString(entry.Key);
                        values.Write(ref writer, entry.Value, depth);
                    }
                }
            }
            else if (value.Reference is IReadOnlyDictionary<string, AvroValue> map)
            {
                if (map.Count > 0)
                {
                    writer.WriteBlockCount(map.Count);
                    foreach (var entry in map)
                    {
                        writer.WriteString(entry.Key);
                        values.Write(ref writer, entry.Value, depth);
                    }
                }
            }
            else
            {
                throw new AvroException($"A {value.Kind} value cannot be written as a map.");
            }

            writer.WriteBlockEnd();
        }
    }

    private sealed class UnionNode(UnionSchema schema, WriterNode[] branches) : WriterNode
    {
        private readonly UnionBranchSelector _selector = new(schema);

        public override void Write(ref AvroWriter writer, in AvroValue value, int depth)
        {
            var index = _selector.IndexOf(value);
            if (index < 0)
            {
                throw Mismatch(schema, value);
            }

            writer.WriteUnionIndex(index);
            branches[index].Write(ref writer, value, depth);
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
