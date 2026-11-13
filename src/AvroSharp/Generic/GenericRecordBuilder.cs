using System;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Builds a <see cref="GenericRecord"/> field by field, as Apache Avro Java's <c>GenericRecordBuilder</c> does: a field
/// left unset takes its schema default, and a field with neither a value nor a default is an error.
/// </summary>
/// <example>
/// <code>
/// var user = new GenericRecordBuilder(schema).Set("id", 1).Set("name", "Ada").Build();
/// </code>
/// </example>
/// <seealso cref="GenericRecord.CreateWithDefaults(RecordSchema)"/>
public sealed class GenericRecordBuilder
{
    private readonly AvroValue[] _values;
    private readonly bool[] _set;

    /// <summary>Initializes a builder with no field set.</summary>
    /// <param name="schema">The record schema.</param>
    public GenericRecordBuilder(RecordSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        _values = new AvroValue[schema.Fields.Count];
        _set = new bool[schema.Fields.Count];
    }

    /// <summary>Gets the record schema.</summary>
    public RecordSchema Schema { get; }

    /// <summary>Sets a field by name. The value is not copied or checked against the field's schema.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The record has no such field.</exception>
    public GenericRecordBuilder Set(string name, AvroValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Set(Schema.GetField(name).Position, value);
    }

    /// <summary>Sets a field by position. The value is not copied or checked against the field's schema.</summary>
    /// <param name="position">The zero-based field position.</param>
    /// <param name="value">The value.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The record has no field at that position.</exception>
    public GenericRecordBuilder Set(int position, AvroValue value)
    {
        if ((uint)position >= (uint)_values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Record '{Schema.FullName}' has {_values.Length} fields.");
        }

        _values[position] = value;
        _set[position] = true;
        return this;
    }

    /// <summary>Gets whether a field has been set.</summary>
    /// <param name="name">The field name.</param>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The record has no such field.</exception>
    public bool IsSet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _set[Schema.GetField(name).Position];
    }

    /// <summary>
    /// Creates a record from the fields set so far, and the defaults of the others. Each call creates a new record
    /// with its own copy of each default; the values set are shared, not copied.
    /// </summary>
    /// <exception cref="AvroException">A field is not set and has no default; the message names it.</exception>
    public GenericRecord Build()
    {
        var record = new GenericRecord(Schema);
        foreach (var field in Schema.Fields)
        {
            var position = field.Position;
            if (_set[position])
            {
                record[position] = _values[position];
            }
            else if (field.DefaultValue is { } defaultValue)
            {
                record[position] = GenericDatumJsonReader.ReadDefault(field.Schema, defaultValue);
            }
            else
            {
                throw new AvroException($"Field '{field.Name}' of record '{Schema.FullName}' is not set and has no default.");
            }
        }

        return record;
    }
}
