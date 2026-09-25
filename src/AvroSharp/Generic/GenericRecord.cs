using System;
using System.Collections.Generic;
using System.Linq;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>A record in the generic data model: a schema and one <see cref="AvroValue"/> per field.</summary>
public sealed class GenericRecord : IEquatable<GenericRecord>
{
    private readonly AvroValue[] _values;

    /// <summary>Initializes a record whose fields are all <c>null</c>.</summary>
    /// <param name="schema">The record schema.</param>
    public GenericRecord(RecordSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        _values = schema.Fields.Count == 0 ? [] : new AvroValue[schema.Fields.Count];
    }

    /// <summary>Gets the record schema.</summary>
    public RecordSchema Schema { get; }

    /// <summary>Gets the values, in field order.</summary>
    public IReadOnlyList<AvroValue> Values => _values;

    /// <summary>Gets or sets a field value by position.</summary>
    /// <param name="position">The zero-based field position.</param>
    public AvroValue this[int position]
    {
        get => _values[position];
        set => _values[position] = value;
    }

    /// <summary>Gets or sets a field value by name.</summary>
    /// <param name="name">The field name.</param>
    /// <exception cref="KeyNotFoundException">The record has no such field.</exception>
    public AvroValue this[string name]
    {
        get => _values[Schema.GetField(name).Position];
        set => _values[Schema.GetField(name).Position] = value;
    }

    /// <summary>Gets a field value by name, if the field exists.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, when found.</param>
    public bool TryGetValue(string name, out AvroValue value)
    {
        if (Schema.TryGetField(name, out var field))
        {
            value = _values[field.Position];
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Compares schema full names and field values (see <see cref="AvroValue.Equals(AvroValue)"/>).</summary>
    /// <param name="other">The other record.</param>
    public bool Equals(GenericRecord? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (Schema.Name == other.Schema.Name && _values.AsSpan().SequenceEqual(other._values)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GenericRecord other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Schema.Name.GetHashCode();

    /// <inheritdoc />
    public override string ToString() =>
        Schema.FullName + " {" + string.Join(", ", Schema.Fields.Select(f => f.Name + ": " + _values[f.Position])) + "}";
}
