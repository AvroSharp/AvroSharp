using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Reads the Avro JSON encoding into <see cref="AvroValue"/>s for one schema. Instances are cached per schema and
/// are thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The input follows the specification (see <see cref="GenericDatumJsonWriter"/>). Record fields may appear in any
/// order; a missing field takes its default value, and a missing field without a default, an unknown field or a
/// repeated field is an error. <c>float</c> and <c>double</c> also accept the strings <c>"NaN"</c>,
/// <c>"Infinity"</c> and <c>"-Infinity"</c>.
/// </para>
/// <para>
/// A union branch is identified by its full name, as the specification requires. For a named type, a name without
/// the namespace is also accepted when exactly one branch has it.
/// </para>
/// </remarks>
public sealed class GenericDatumJsonReader
{
    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumJsonReader> s_cache = new();

    private readonly GenericDatumReaderOptions _options;

    private GenericDatumJsonReader(AvroSchema schema, GenericDatumReaderOptions options)
    {
        Schema = schema;
        _options = options;
    }

    /// <summary>Gets the schema the data was written with.</summary>
    public AvroSchema Schema { get; }

    // Each record level nests at most a few JSON levels (the record, a union wrapper, arrays and maps in between);
    // this bounds the parser before the record depth is checked.
    private int MaxJsonDepth => (int)Math.Min(int.MaxValue, (8L * _options.MaxDepth) + 64);

    /// <summary>
    /// Gets the reader for <paramref name="schema"/>. Readers with the default options are cached per schema; readers
    /// with custom options are created each time, so keep and reuse them.
    /// </summary>
    /// <param name="schema">The schema the data was written with.</param>
    /// <param name="options">Limits for malformed input, or <see langword="null"/> for <see cref="GenericDatumReaderOptions.Default"/>.</param>
    public static GenericDatumJsonReader Create(AvroSchema schema, GenericDatumReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return options is null || ReferenceEquals(options, GenericDatumReaderOptions.Default)
            ? s_cache.GetValue(schema, static s => new GenericDatumJsonReader(s, GenericDatumReaderOptions.Default))
            : new GenericDatumJsonReader(schema, options);
    }

    /// <summary>Reads one value from a JSON string. Nothing but whitespace may follow it.</summary>
    /// <param name="json">The JSON text.</param>
    /// <exception cref="AvroDataException">The JSON is malformed or does not match the schema.</exception>
    public AvroValue Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            throw new AvroDataException("Invalid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            return Convert(document.RootElement);
        }
    }

    /// <summary>Reads one value from UTF-8 JSON. Nothing but whitespace may follow it.</summary>
    /// <param name="utf8Json">The JSON text.</param>
    /// <exception cref="AvroDataException">The JSON is malformed or does not match the schema.</exception>
    public AvroValue Read(ReadOnlySpan<byte> utf8Json)
    {
        if (!Utf8Validation.IsValid(utf8Json))
        {
            throw new AvroDataException("The JSON is not valid UTF-8.");
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = MaxJsonDepth });
        var value = Read(ref reader);
        try
        {
            // The reader rejects anything but whitespace after a complete top-level value.
            reader.Read();
        }
        catch (JsonException ex)
        {
            throw new AvroDataException("Unexpected JSON after the value: " + ex.Message, ex);
        }

        return value;
    }

    /// <summary>
    /// Reads the next JSON value from <paramref name="reader"/>, for example one line of a stream of values. The
    /// reader is left on the last token of the value.
    /// </summary>
    /// <param name="reader">The source, positioned before or on the first token of the value.</param>
    /// <exception cref="AvroDataException">The JSON is malformed or does not match the schema.</exception>
    public AvroValue Read(ref Utf8JsonReader reader)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new AvroDataException("Invalid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            try
            {
                return Convert(document.RootElement);
            }
            catch (InvalidOperationException ex)
            {
                // The caller's reader was not validated up front: JsonElement reports invalid UTF-8 in a string only
                // when the string is transcoded. Every other JsonElement access checks the value kind first.
                throw new AvroDataException("The JSON contains a string that is not valid UTF-8.", ex);
            }
        }
    }

    /// <summary>
    /// Converts a field's default value, as written in the schema (<see cref="RecordField.DefaultValue"/>), to an
    /// <see cref="AvroValue"/>: the value a reader gives the field when the data lacks it. Defaults use the JSON
    /// encoding with one difference: a union default is not wrapped, and takes the first branch it matches.
    /// </summary>
    /// <param name="schema">The field's schema.</param>
    /// <param name="value">The default, as written in the schema.</param>
    /// <exception cref="AvroDataException">The default does not match the schema.</exception>
    public static AvroValue ReadDefault(AvroSchema schema, JsonElement value) =>
        new Converter(GenericDatumReaderOptions.Default.MaxDepth, wrappedUnions: false).Convert(schema, value);

    private AvroValue Convert(JsonElement root)
    {
        try
        {
            var converter = new Converter(_options.MaxDepth, wrappedUnions: true);
            return converter.Convert(Schema, root);
        }
        catch (AvroDataException ex) when (ErrorPath.Get(ex) is { } path)
        {
            throw new AvroDataException($"At {ErrorPath.DescribeJson(path)}: {ex.Message}", ex);
        }
    }

    private static AvroDataException Expected(string what, JsonElement actual) =>
        new($"Expected {what}, found {Describe(actual)}.");

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "the number " + element.GetRawText(),
        JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString(),
    };

    /// <summary>Walks the schema and the JSON together. Not a compiled tree: JSON reading is not a hot path.</summary>
    private struct Converter(int maxDepth, bool wrappedUnions)
    {
        private int _depth;

        public AvroValue Convert(AvroSchema schema, JsonElement json) => schema switch
        {
            RecordSchema record => ConvertRecord(record, json),
            EnumSchema enumSchema => ConvertEnum(enumSchema, json),
            FixedSchema fixedSchema => new GenericFixed(fixedSchema, ByteString(json, fixedSchema.Size)),
            ArraySchema array => ConvertArray(array, json),
            MapSchema map => ConvertMap(map, json),
            UnionSchema union => wrappedUnions ? ConvertWrappedUnion(union, json) : ConvertDefaultUnion(union, json),
            _ => ConvertPrimitive(schema.Type, json),
        };

        private static AvroValue ConvertPrimitive(AvroSchemaType type, JsonElement json)
        {
            switch (type)
            {
                case AvroSchemaType.Null:
                    return json.ValueKind == JsonValueKind.Null ? AvroValue.Null : throw Expected("null", json);
                case AvroSchemaType.Boolean:
                    return json.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw Expected("a boolean", json),
                    };
                case AvroSchemaType.Int:
                    return json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var i) ? i : throw Expected("an int", json);
                case AvroSchemaType.Long:
                    return json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var l) ? l : throw Expected("a long", json);
                case AvroSchemaType.Float:
                    if (json.ValueKind == JsonValueKind.Number && json.TryGetSingle(out var f))
                    {
                        return f;
                    }

                    return json.ValueKind == JsonValueKind.String && AvroJsonConventions.TryParseNonFinite(json.GetString(), out var nf)
                        ? (float)nf
                        : throw Expected("a float", json);
                case AvroSchemaType.Double:
                    if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var d))
                    {
                        return d;
                    }

                    return json.ValueKind == JsonValueKind.String && AvroJsonConventions.TryParseNonFinite(json.GetString(), out var nd)
                        ? nd
                        : throw Expected("a double", json);
                case AvroSchemaType.Bytes:
                    return ByteString(json, -1);
                default:
                    return json.ValueKind == JsonValueKind.String ? json.GetString() : throw Expected("a string", json);
            }
        }

        private static byte[] ByteString(JsonElement json, int size)
        {
            if (json.ValueKind != JsonValueKind.String)
            {
                throw Expected(size < 0 ? "a byte string" : $"a byte string of length {size}", json);
            }

            var bytes = AvroJsonConventions.ParseByteString(json.GetString()!)
                ?? throw new AvroDataException("A byte string contains a character above U+00FF.");
            return size < 0 || bytes.Length == size
                ? bytes
                : throw new AvroDataException($"Expected a byte string of length {size}, found length {bytes.Length}.");
        }

        private static AvroValue ConvertEnum(EnumSchema schema, JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.String)
            {
                throw Expected($"a symbol of enum '{schema.FullName}'", json);
            }

            var symbol = json.GetString()!;
            return schema.TryGetOrdinal(symbol, out var ordinal)
                ? AvroValue.FromEnumUnchecked(schema, ordinal)
                : throw new AvroDataException($"'{symbol}' is not a symbol of enum '{schema.FullName}'.");
        }

        private AvroValue ConvertRecord(RecordSchema schema, JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object)
            {
                throw Expected($"an object for record '{schema.FullName}'", json);
            }

            if (++_depth > maxDepth)
            {
                throw new AvroDataException($"Records are nested more than {maxDepth} levels deep (GenericDatumReaderOptions.MaxDepth).");
            }

            var fields = schema.Fields;
            var record = new GenericRecord(schema, fields.Count);
            Span<bool> seen = fields.Count <= 64 ? stackalloc bool[64] : new bool[fields.Count];
            foreach (var property in json.EnumerateObject())
            {
                if (!schema.TryGetField(property.Name, out var field))
                {
                    throw new AvroDataException($"Record '{schema.FullName}' has no field '{property.Name}'.");
                }

                if (seen[field.Position])
                {
                    throw new AvroDataException($"Field '{property.Name}' appears more than once.");
                }

                seen[field.Position] = true;
                try
                {
                    record.ValueAt(field.Position) = Convert(field.Schema, property.Value);
                }
                catch (AvroDataException ex) when (ErrorPath.Add(ex, "." + property.Name))
                {
                    throw;
                }
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (!seen[i])
                {
                    var field = fields[i];
                    record.ValueAt(i) = field.DefaultValue is { } defaultValue
                        ? new Converter(maxDepth - _depth, wrappedUnions: false).Convert(field.Schema, defaultValue)
                        : throw new AvroDataException($"Field '{field.Name}' of record '{schema.FullName}' is missing and has no default value.");
                }
            }

            _depth--;
            return record;
        }

        private AvroValue ConvertArray(ArraySchema schema, JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Array)
            {
                throw Expected("an array", json);
            }

            var list = new List<AvroValue>(json.GetArrayLength());
            foreach (var item in json.EnumerateArray())
            {
                try
                {
                    list.Add(Convert(schema.Items, item));
                }
                catch (AvroDataException ex) when (ErrorPath.Add(ex, "[" + list.Count + "]"))
                {
                    throw;
                }
            }

            return AvroValue.FromArray(list);
        }

        private AvroValue ConvertMap(MapSchema schema, JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object)
            {
                throw Expected("an object for a map", json);
            }

            var map = new Dictionary<string, AvroValue>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                try
                {
                    map[property.Name] = Convert(schema.Values, property.Value);
                }
                catch (AvroDataException ex) when (ErrorPath.Add(ex, "['" + property.Name + "']"))
                {
                    throw;
                }
            }

            return AvroValue.FromMap(map);
        }

        private AvroValue ConvertWrappedUnion(UnionSchema schema, JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Null)
            {
                foreach (var branch in schema.Branches)
                {
                    if (branch.Type == AvroSchemaType.Null)
                    {
                        return AvroValue.Null;
                    }
                }

                throw new AvroDataException($"null is not allowed: the union {schema.CanonicalForm} has no null branch.");
            }

            if (json.ValueKind != JsonValueKind.Object)
            {
                throw Expected("null or an object naming a union branch", json);
            }

            JsonProperty? only = null;
            foreach (var property in json.EnumerateObject())
            {
                if (only is not null)
                {
                    throw new AvroDataException("A union value must be an object with exactly one property, the branch name.");
                }

                only = property;
            }

            if (only is not { } wrapped)
            {
                throw new AvroDataException("A union value must be an object with exactly one property, the branch name.");
            }

            var match = FindBranch(schema, wrapped.Name)
                ?? throw new AvroDataException($"'{wrapped.Name}' is not a branch of the union {schema.CanonicalForm}.");
            try
            {
                return Convert(match, wrapped.Value);
            }
            catch (AvroDataException ex) when (ErrorPath.Add(ex, "." + wrapped.Name))
            {
                throw;
            }
        }

        private static AvroSchema? FindBranch(UnionSchema schema, string name)
        {
            foreach (var branch in schema.Branches)
            {
                if (string.Equals(AvroJsonConventions.BranchName(branch), name, StringComparison.Ordinal))
                {
                    return branch;
                }
            }

            // A short name for a named type, when it is unambiguous.
            AvroSchema? found = null;
            foreach (var branch in schema.Branches)
            {
                if (branch is NamedSchema named && string.Equals(named.Name.Name, name, StringComparison.Ordinal))
                {
                    if (found is not null)
                    {
                        return null;
                    }

                    found = branch;
                }
            }

            return found;
        }

        private AvroValue ConvertDefaultUnion(UnionSchema schema, JsonElement json)
        {
            // Avro 1.12: the default corresponds to the first branch it matches.
            foreach (var branch in schema.Branches)
            {
                if (DefaultValueValidator.IsValid(branch, json))
                {
                    return Convert(branch, json);
                }
            }

            throw new AvroDataException($"The default value matches no branch of the union {schema.CanonicalForm}.");
        }
    }
}
