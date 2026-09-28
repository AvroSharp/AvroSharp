using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Replaces a value found inside a record field. Receives the field it is in and the value; returns the value to keep,
/// which may be the same one.
/// </summary>
/// <param name="field">The record field the value is in, and the value's own schema.</param>
/// <param name="value">The value.</param>
public delegate AvroValue AvroFieldTransform(in AvroFieldContext field, in AvroValue value);

/// <summary>Where a value handed to an <see cref="AvroFieldTransform"/> is: the record field, and the value's schema.</summary>
public readonly struct AvroFieldContext : IEquatable<AvroFieldContext>
{
    internal AvroFieldContext(RecordSchema record, RecordField field, AvroSchema schema)
    {
        Record = record;
        Field = field;
        Schema = schema;
    }

    /// <summary>Gets the record whose field holds the value.</summary>
    public RecordSchema Record { get; }

    /// <summary>Gets the field that holds the value, with its properties (for example the tags that data rules match).</summary>
    public RecordField Field { get; }

    /// <summary>
    /// Gets the value's own schema: the field's type, or for a value in an array, map or union, the item, value or
    /// branch schema it has.
    /// </summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the field's full name: the record's full name, a dot, and the field's name, as Confluent's data rules name fields.</summary>
    public string FullName => Record.FullName + "." + Field.Name;

    /// <summary>Compares two contexts: equal when they name the same record, field and schema instances.</summary>
    /// <param name="left">The first context.</param>
    /// <param name="right">The second context.</param>
    public static bool operator ==(AvroFieldContext left, AvroFieldContext right) => left.Equals(right);

    /// <summary>Compares two contexts: different unless they name the same record, field and schema instances.</summary>
    /// <param name="left">The first context.</param>
    /// <param name="right">The second context.</param>
    public static bool operator !=(AvroFieldContext left, AvroFieldContext right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(AvroFieldContext other) =>
        ReferenceEquals(Record, other.Record) && ReferenceEquals(Field, other.Field) && ReferenceEquals(Schema, other.Schema);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AvroFieldContext other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        unchecked((((RuntimeHelpers.GetHashCode(Record) * 397) ^ RuntimeHelpers.GetHashCode(Field)) * 397) ^ RuntimeHelpers.GetHashCode(Schema));
}

/// <summary>
/// Walks a generic value together with its schema and replaces values inside it: the basis for field-level data
/// rules, encryption and redaction.
/// </summary>
/// <remarks>
/// <para>
/// The transform is called for every non-null value of a primitive, enum or fixed type inside a record field, in
/// schema order, with that field's <see cref="AvroFieldContext"/>. The walk goes through records, arrays, maps and
/// union branches (resolved as <see cref="GenericDatumWriter"/> resolves them); values of arrays and maps get the
/// context of the field that holds the array or map.
/// </para>
/// <para>
/// Nothing is changed in place. A record, array or map is copied only when a value inside it is replaced, so when the
/// transform returns every value unchanged, the result is the same instance as the input.
/// </para>
/// </remarks>
public static class AvroValueTransformer
{
    private static readonly ConditionalWeakTable<UnionSchema, UnionBranchSelector> s_selectors = new();

    /// <summary>Returns <paramref name="value"/> with the values that <paramref name="transform"/> replaces.</summary>
    /// <param name="schema">The value's schema.</param>
    /// <param name="value">The value.</param>
    /// <param name="transform">Called for each value inside a record field; returns the value to keep.</param>
    /// <param name="maxDepth">The deepest nesting of records, arrays, maps and unions accepted, so a cyclic record cannot recurse without bound.</param>
    /// <exception cref="AvroException">The value does not match the schema, or it is nested more deeply than <paramref name="maxDepth"/>.</exception>
    public static AvroValue Transform(AvroSchema schema, in AvroValue value, AvroFieldTransform transform, int maxDepth = 128)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        return new Walker(transform, maxDepth).Walk(schema, value, field: null, record: null, depth: 0);
    }

    private static bool IsSame(in AvroValue a, in AvroValue b) => ReferenceEquals(a.Reference, b.Reference) && a.Bits == b.Bits;

    private readonly struct Walker(AvroFieldTransform transform, int maxDepth)
    {
        public AvroValue Walk(AvroSchema schema, in AvroValue value, RecordField? field, RecordSchema? record, int depth)
        {
            if (value.Kind == AvroValueKind.Null)
            {
                return value;
            }

            if (depth > maxDepth)
            {
                throw new AvroException($"The value is nested more deeply than {maxDepth} levels.");
            }

            switch (schema)
            {
                case RecordSchema recordSchema:
                    return WalkRecord(recordSchema, value, depth);
                case ArraySchema array:
                    return WalkArray(array, value, field, record, depth);
                case MapSchema map:
                    return WalkMap(map, value, field, record, depth);
                case UnionSchema union:
                    var index = s_selectors.GetValue(union, static u => new UnionBranchSelector(u)).IndexOf(value);
                    return index >= 0
                        ? Walk(union.Branches[index], value, field, record, depth + 1)
                        : throw new AvroException($"A {value.Kind} value matches no branch of the union {union.ToJson()}.");
                default:
                    return field is null || record is null ? value : transform(new AvroFieldContext(record, field, schema), value);
            }
        }

        private AvroValue WalkRecord(RecordSchema schema, in AvroValue value, int depth)
        {
            var input = value.AsRecord();
            GenericRecord? output = null;
            for (var i = 0; i < schema.Fields.Count; i++)
            {
                var field = schema.Fields[i];
                var original = input[i];
                var replaced = Walk(field.Schema, original, field, schema, depth + 1);
                if (output is null && !IsSame(original, replaced))
                {
                    output = new GenericRecord(schema);
                    for (var j = 0; j < i; j++)
                    {
                        output[j] = input[j];
                    }
                }

                if (output is not null)
                {
                    output[i] = replaced;
                }
            }

            return output is null ? value : output;
        }

        private AvroValue WalkArray(ArraySchema schema, in AvroValue value, RecordField? field, RecordSchema? record, int depth)
        {
            var items = value.AsArray();
            AvroValue[]? output = null;
            for (var i = 0; i < items.Count; i++)
            {
                var replaced = Walk(schema.Items, items[i], field, record, depth + 1);
                if (output is null && !IsSame(items[i], replaced))
                {
                    output = new AvroValue[items.Count];
                    for (var j = 0; j < i; j++)
                    {
                        output[j] = items[j];
                    }
                }

                if (output is not null)
                {
                    output[i] = replaced;
                }
            }

            return output is null ? value : AvroValue.FromArray(output);
        }

        private AvroValue WalkMap(MapSchema schema, in AvroValue value, RecordField? field, RecordSchema? record, int depth)
        {
            var entries = value.AsMap();
            Dictionary<string, AvroValue>? output = null;
            foreach (var entry in entries)
            {
                var replaced = Walk(schema.Values, entry.Value, field, record, depth + 1);
                if (output is null && !IsSame(entry.Value, replaced))
                {
                    output = new Dictionary<string, AvroValue>(entries.Count, StringComparer.Ordinal);
                    foreach (var earlier in entries)
                    {
                        if (string.Equals(earlier.Key, entry.Key, StringComparison.Ordinal))
                        {
                            break;
                        }

                        output[earlier.Key] = earlier.Value;
                    }
                }

                if (output is not null)
                {
                    output[entry.Key] = replaced;
                }
            }

            return output is null ? value : AvroValue.FromMap(output);
        }
    }
}
