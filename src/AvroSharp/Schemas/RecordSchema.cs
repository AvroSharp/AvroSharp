using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>A record schema: a named sequence of fields.</summary>
/// <seealso cref="RecordField"/>
/// <seealso cref="AvroSharp.Generic.GenericRecord"/>
public sealed class RecordSchema : NamedSchema
{
    // Linear search beats hashing for small records; the index is built on first use beyond that.
    private const int IndexThreshold = 8;

    private RecordField[] _fields = [];
    private Dictionary<string, RecordField>? _index;

    /// <summary>Initializes a record schema.</summary>
    /// <param name="name">The record name.</param>
    /// <param name="fields">The fields; each instance may belong to only one record.</param>
    /// <param name="doc">Optional documentation.</param>
    /// <param name="aliases">Optional alternative names.</param>
    /// <param name="isError"><see langword="true"/> for a protocol <c>error</c> type.</param>
    /// <param name="properties">Optional custom properties.</param>
    /// <exception cref="AvroSchemaException">Two fields have the same name, or a field already belongs to another record.</exception>
    public RecordSchema(
        SchemaName name,
        IEnumerable<RecordField> fields,
        string? doc = null,
        IEnumerable<SchemaName>? aliases = null,
        bool isError = false,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
        : this(name, doc, aliases, isError, properties)
    {
        ArgumentNullException.ThrowIfNull(fields);
        SetFields(fields);
    }

    internal RecordSchema(SchemaName name, string? doc, IEnumerable<SchemaName>? aliases, bool isError, IReadOnlyDictionary<string, JsonElement>? properties)
        : base(AvroSchemaType.Record, name, aliases, doc, logicalType: null, properties)
    {
        IsError = isError;
    }

    /// <summary>Gets the fields, in declaration order.</summary>
    public IReadOnlyList<RecordField> Fields => _fields;

    /// <summary>Gets a value indicating whether this is a protocol <c>error</c> type rather than a <c>record</c>.</summary>
    public bool IsError { get; }

    /// <summary>
    /// Creates a record whose fields may refer to the record itself, such as a linked list node.
    /// </summary>
    /// <param name="name">The record name.</param>
    /// <param name="fields">Creates the fields, given the record being defined.</param>
    /// <param name="doc">Optional documentation.</param>
    /// <param name="aliases">Optional alternative names.</param>
    /// <param name="isError"><see langword="true"/> for a protocol <c>error</c> type.</param>
    /// <param name="properties">Optional custom properties.</param>
    public static RecordSchema CreateRecursive(
        SchemaName name,
        Func<RecordSchema, IEnumerable<RecordField>> fields,
        string? doc = null,
        IEnumerable<SchemaName>? aliases = null,
        bool isError = false,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var record = new RecordSchema(name, doc, aliases, isError, properties);
        record.SetFields(fields(record));
        return record;
    }

    /// <summary>Looks up a field by name.</summary>
    /// <param name="name">The field name (case-sensitive).</param>
    /// <param name="field">The field, when found.</param>
    public bool TryGetField(string name, [NotNullWhen(true)] out RecordField? field)
    {
        var fields = _fields;
        if (fields.Length <= IndexThreshold)
        {
            foreach (var candidate in fields)
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    field = candidate;
                    return true;
                }
            }

            field = null;
            return false;
        }

        var index = _index ??= fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return index.TryGetValue(name, out field);
    }

    /// <summary>Gets a field by name.</summary>
    /// <param name="name">The field name (case-sensitive).</param>
    /// <exception cref="KeyNotFoundException">The record has no such field.</exception>
    public RecordField GetField(string name) =>
        TryGetField(name, out var field) ? field : throw new KeyNotFoundException($"Record '{FullName}' has no field '{name}'.");

    internal void SetFields(IEnumerable<RecordField> fields)
    {
        var array = fields.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Length; i++)
        {
            var field = array[i] ?? throw new AvroSchemaException($"Record '{FullName}' has a null field.");
            if (!names.Add(field.Name))
            {
                throw new AvroSchemaException($"Record '{FullName}' has more than one field named '{field.Name}'.");
            }

            array[i] = field.Attach(this, i);
        }

        _fields = array;
    }
}
