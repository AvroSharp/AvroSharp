using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>A field of a <see cref="RecordSchema"/>.</summary>
/// <remarks>
/// A field belongs to one record, which sets its <see cref="Record"/> and <see cref="Position"/>. A record given a
/// field that already belongs to another one takes a copy, so <c>new RecordSchema(name, other.Fields.Append(field))</c>
/// works and leaves <c>other</c> as it was.
/// </remarks>
public sealed class RecordField
{
    private static readonly IReadOnlyDictionary<string, JsonElement> s_noProperties = new Dictionary<string, JsonElement>(0, StringComparer.Ordinal);

    /// <summary>Initializes a record field.</summary>
    /// <param name="name">The field name; it must be a valid Avro name.</param>
    /// <param name="schema">The field schema.</param>
    /// <param name="defaultValue">
    /// The JSON default value used during schema resolution, or <see langword="null"/> for no default. Its form
    /// follows the specification's table (for example a string of code points 0-255 for <c>bytes</c>).
    /// </param>
    /// <param name="doc">Optional documentation.</param>
    /// <param name="order">The sort order.</param>
    /// <param name="aliases">Optional alternative names.</param>
    /// <param name="properties">Optional custom properties.</param>
    /// <exception cref="AvroSchemaException">The name is not valid.</exception>
    public RecordField(
        string name,
        AvroSchema schema,
        JsonElement? defaultValue = null,
        string? doc = null,
        FieldOrder order = FieldOrder.Ascending,
        IEnumerable<string>? aliases = null,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
        : this(name, schema, defaultValue, doc, order, aliases?.ToArray(), properties, validate: true)
    {
    }

    internal RecordField(
        string name,
        AvroSchema schema,
        JsonElement? defaultValue,
        string? doc,
        FieldOrder order,
        string[]? aliases,
        IReadOnlyDictionary<string, JsonElement>? properties,
        bool validate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);

        if (validate && !AvroNames.IsValidName(name.AsSpan()))
        {
            throw new AvroSchemaException($"'{name}' is not a valid field name: it must start with [A-Za-z_] and contain only [A-Za-z0-9_].");
        }

        if (defaultValue is { ValueKind: JsonValueKind.Undefined })
        {
            defaultValue = null;
        }

        Name = name;
        Schema = schema;
        DefaultValue = defaultValue;
        Doc = doc;
        Order = order;
        Aliases = aliases ?? [];
        Properties = properties is null || properties.Count == 0 ? s_noProperties : properties;
        Position = -1;
    }

    /// <summary>Gets the field name.</summary>
    public string Name { get; }

    /// <summary>Gets the field schema.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the JSON default value, or <see langword="null"/> when the field has no default.</summary>
    /// <seealso cref="AvroSharp.Generic.GenericDatumJsonReader.ReadDefault(AvroSchema, JsonElement)"/>
    public JsonElement? DefaultValue { get; }

    /// <summary>Gets a value indicating whether the field has a default value.</summary>
    public bool HasDefaultValue => DefaultValue.HasValue;

    /// <summary>Gets the documentation, or <see langword="null"/>.</summary>
    public string? Doc { get; }

    /// <summary>Gets the sort order.</summary>
    public FieldOrder Order { get; }

    /// <summary>Gets the alternative names of the field.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Gets the attributes not defined by the specification for fields.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; }

    /// <summary>Gets the zero-based position of the field in its record, or -1 before it is added to one.</summary>
    public int Position { get; private set; }

    /// <summary>Gets the record this field belongs to, or <see langword="null"/> before it is added to one.</summary>
    public RecordSchema? Record { get; private set; }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Schema.CanonicalForm}";

    // Attaches this field to its record, or a copy when it already belongs to one.
    internal RecordField Attach(RecordSchema record, int position)
    {
        var field = Record is null ? this : new RecordField(Name, Schema, DefaultValue, Doc, Order, [.. Aliases], Properties, validate: false);
        field.Record = record;
        field.Position = position;
        return field;
    }
}
