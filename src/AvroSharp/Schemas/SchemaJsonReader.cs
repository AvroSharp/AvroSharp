using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>
/// Builds schemas from a parsed JSON value. Attribute names are matched against UTF-8 literals, so reading a
/// schema allocates only the strings and objects the schema itself keeps.
/// </summary>
internal sealed class SchemaJsonReader
{
    private readonly AvroSchemaParseOptions _options;
    private readonly Dictionary<string, NamedSchema> _committed;
    private readonly Dictionary<string, NamedSchema> _pending = new(StringComparer.Ordinal);
    private readonly List<JsonPathSegment> _path = [];

    // _pathNodes[i] ends in _path[i]; built only when a path is kept (KeepPath), and shared by the paths kept after.
    private readonly List<PathNode> _pathNodes = [];
    private readonly List<PendingDefault> _defaults = [];

    // Names that an earlier parse committed and this schema defines again (AllowIdenticalRedefinitions).
    private readonly List<(NamedSchema Definition, NamedSchema Existing, PathNode? Path)> _redefinitions = [];

    public SchemaJsonReader(AvroSchemaParseOptions options, Dictionary<string, NamedSchema> committed)
    {
        _options = options;
        _committed = committed;
    }

    public AvroSchema Read(JsonElement root)
    {
        var schema = ReadSchema(root, enclosingNamespace: null);

        // A repeated definition must describe the same type; compared once the whole schema exists.
        foreach (var (definition, existing, path) in _redefinitions)
        {
            if (!string.Equals(definition.CanonicalForm, existing.CanonicalForm, StringComparison.Ordinal))
            {
                throw new ParseError(
                    $"The name '{definition.FullName}' is already defined differently: {existing.CanonicalForm}, not {definition.CanonicalForm}.",
                    PathNode.ToArray(path));
            }
        }

        // Defaults are checked once the whole schema exists, so a default may use a record that is still
        // being defined where the default appears (recursive types).
        if (_options.ValidateDefaults)
        {
            foreach (var pending in _defaults)
            {
                if (!DefaultValueValidator.IsValid(pending.Schema, pending.Value))
                {
                    throw new ParseError(
                        $"The default value {Abbreviate(pending.Value)} of field '{pending.FieldName}' does not match its schema {pending.Schema.CanonicalForm}.",
                        PathNode.ToArray(pending.Path));
                }
            }
        }

        return schema;
    }

    public void Commit()
    {
        foreach (var pair in _pending)
        {
            // A repeated identical definition keeps the first one.
            if (!_committed.ContainsKey(pair.Key))
            {
                _committed.Add(pair.Key, pair.Value);
            }
        }
    }

    private static string Abbreviate(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 61).ToString(), "...");
    }

    private AvroSchema ReadSchema(JsonElement element, string? enclosingNamespace) => element.ValueKind switch
    {
        JsonValueKind.String => ResolveName(element.GetString()!, enclosingNamespace),
        JsonValueKind.Object => ReadObject(element, enclosingNamespace),
        JsonValueKind.Array => ReadUnion(element, enclosingNamespace),
        _ => throw Error("A schema must be a JSON string (a type name), object or array (a union)."),
    };

    private AvroSchema ResolveName(string name, string? enclosingNamespace)
    {
        if (AvroNames.TryGetPrimitiveType(name, out var primitive))
        {
            return PrimitiveSchema.Get(primitive);
        }

        // A simple name is relative to the enclosing namespace; like the reference implementations,
        // fall back to the null namespace when no such type exists.
        if (enclosingNamespace is not null && name.IndexOf('.', StringComparison.Ordinal) < 0
            && TryGetNamed(string.Concat(enclosingNamespace, ".", name), out var qualified))
        {
            return qualified;
        }

        if (TryGetNamed(name, out var named))
        {
            return named;
        }

        throw Error($"'{name}' is not a defined type. Named types must be defined before they are used.");
    }

    private bool TryGetNamed(string fullName, out NamedSchema schema) =>
        _pending.TryGetValue(fullName, out schema!) || _committed.TryGetValue(fullName, out schema!);

    private AvroSchema ReadObject(JsonElement element, string? enclosingNamespace)
    {
        if (!element.TryGetProperty("type"u8, out var type))
        {
            throw Error("A schema object must have a 'type' attribute.");
        }

        if (type.ValueKind != JsonValueKind.String)
        {
            Push("type");
            throw Error("The 'type' attribute of a schema object must be a string.");
        }

        if (type.ValueEquals("record"u8))
        {
            return ReadRecord(element, enclosingNamespace, isError: false);
        }

        if (type.ValueEquals("error"u8))
        {
            return ReadRecord(element, enclosingNamespace, isError: true);
        }

        if (type.ValueEquals("enum"u8))
        {
            return ReadEnum(element, enclosingNamespace);
        }

        if (type.ValueEquals("array"u8))
        {
            return ReadArray(element, enclosingNamespace);
        }

        if (type.ValueEquals("map"u8))
        {
            return ReadMap(element, enclosingNamespace);
        }

        if (type.ValueEquals("fixed"u8))
        {
            return ReadFixed(element, enclosingNamespace);
        }

        var typeName = type.GetString()!;
        if (AvroNames.TryGetPrimitiveType(typeName, out var primitive))
        {
            return ReadPrimitive(element, primitive);
        }

        // {"type": "com.example.Foo"} refers to a named type.
        Push("type");
        var resolved = ResolveName(typeName, enclosingNamespace);
        Pop();
        return resolved;
    }

    private static PrimitiveSchema ReadPrimitive(JsonElement element, AvroSchemaType type)
    {
        if (TryReadLogicalOnly(element, type) is { } logical)
        {
            return logical;
        }

        Dictionary<string, JsonElement>? properties = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals("type"u8))
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (properties is null)
        {
            return PrimitiveSchema.Get(type);
        }

        var logicalType = TakeLogicalType(properties, type, fixedSize: -1);
        return new PrimitiveSchema(type, logicalType, Keep(properties));
    }

    // {"type":"long","logicalType":"timestamp-micros"}, the usual shape, read without a dictionary of properties
    // (#135). Anything else (a decimal, other properties, a logical type that does not apply) takes the general path.
    private static PrimitiveSchema? TryReadLogicalOnly(JsonElement element, AvroSchemaType type)
    {
        JsonElement? logicalName = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type"u8))
            {
                continue;
            }

            if (!property.NameEquals("logicalType"u8) || property.Value.ValueKind != JsonValueKind.String || property.Value.ValueEquals("decimal"u8))
            {
                return null;
            }

            logicalName = property.Value;
        }

        return logicalName is { } name
            && AvroLogicalType.FromName(name.GetString()!) is { } logicalType
            && IsValidTarget(logicalType, type, fixedSize: -1)
            ? new PrimitiveSchema(type, logicalType, null)
            : null;
    }

    private RecordSchema ReadRecord(JsonElement element, string? enclosingNamespace, bool isError)
    {
        var name = ReadName(element, enclosingNamespace);
        string? doc = null;
        IEnumerable<SchemaName>? aliases = null;
        JsonElement fields = default;
        Dictionary<string, JsonElement>? properties = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type"u8) || property.NameEquals("name"u8) || property.NameEquals("namespace"u8))
            {
                continue;
            }

            if (property.NameEquals("doc"u8))
            {
                doc = ReadString(property);
            }
            else if (property.NameEquals("aliases"u8))
            {
                aliases = ReadAliases(property, name.Namespace);
            }
            else if (property.NameEquals("fields"u8))
            {
                fields = property.Value;
            }
            else
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (fields.ValueKind != JsonValueKind.Array)
        {
            if (fields.ValueKind != JsonValueKind.Undefined)
            {
                Push("fields");
            }

            throw Error($"Record '{name.FullName}' must have a 'fields' array.");
        }

        RecordSchema record;
        try
        {
            record = new RecordSchema(name, doc, aliases, isError, Keep(properties));
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Register(record);
        ReadFields(record, fields);
        return record;
    }

    private void ReadFields(RecordSchema record, JsonElement fields)
    {
        Push("fields");
        var list = new List<RecordField>(fields.GetArrayLength());
        var index = 0;
        foreach (var fieldElement in fields.EnumerateArray())
        {
            PushIndex(index++);
            list.Add(ReadField(fieldElement, record.Name.Namespace));
            Pop();
        }

        try
        {
            record.SetFields(list);
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Pop();
    }

    private RecordField ReadField(JsonElement element, string? recordNamespace)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error("A record field must be a JSON object.");
        }

        var attributes = ReadFieldAttributes(element);
        var name = attributes.Name ?? throw Error("A record field must have a 'name'.");
        if (attributes.Type.ValueKind == JsonValueKind.Undefined)
        {
            throw Error($"Field '{name}' must have a 'type'.");
        }

        Push("type");
        var schema = ReadSchema(attributes.Type, recordNamespace);
        Pop();

        if (attributes.Default is { } value)
        {
            Push("default");
            _defaults.Add(new PendingDefault(name, schema, value, KeepPath()));
            Pop();
        }

        Push("name");
        RecordField field;
        try
        {
            field = new RecordField(name, schema, attributes.Default, attributes.Doc, attributes.Order, attributes.Aliases, Keep(attributes.Properties), _options.ValidateNames);
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Pop();
        return field;
    }

    private FieldAttributes ReadFieldAttributes(JsonElement element)
    {
        var attributes = new FieldAttributes { Order = FieldOrder.Ascending };
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("name"u8))
            {
                attributes.Name = ReadString(property);
            }
            else if (property.NameEquals("type"u8))
            {
                attributes.Type = property.Value;
            }
            else if (property.NameEquals("default"u8))
            {
                attributes.Default = property.Value.Clone();
            }
            else if (property.NameEquals("doc"u8))
            {
                attributes.Doc = ReadString(property);
            }
            else if (property.NameEquals("order"u8))
            {
                attributes.Order = ReadOrder(property);
            }
            else if (property.NameEquals("aliases"u8))
            {
                attributes.Aliases = ReadStringArray(property);
            }
            else
            {
                (attributes.Properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        return attributes;
    }

    private EnumSchema ReadEnum(JsonElement element, string? enclosingNamespace)
    {
        var name = ReadName(element, enclosingNamespace);
        string? doc = null;
        IEnumerable<SchemaName>? aliases = null;
        string[]? symbols = null;
        string? defaultSymbol = null;
        Dictionary<string, JsonElement>? properties = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type"u8) || property.NameEquals("name"u8) || property.NameEquals("namespace"u8))
            {
                continue;
            }

            if (property.NameEquals("doc"u8))
            {
                doc = ReadString(property);
            }
            else if (property.NameEquals("aliases"u8))
            {
                aliases = ReadAliases(property, name.Namespace);
            }
            else if (property.NameEquals("symbols"u8))
            {
                symbols = ReadStringArray(property);
            }
            else if (property.NameEquals("default"u8))
            {
                defaultSymbol = ReadString(property);
            }
            else
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (symbols is null)
        {
            throw Error($"Enum '{name.FullName}' must have a 'symbols' array.");
        }

        EnumSchema schema;
        try
        {
            schema = new EnumSchema(name, symbols, defaultSymbol, doc, aliases, Keep(properties), _options.ValidateNames);
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Register(schema);
        return schema;
    }

    private FixedSchema ReadFixed(JsonElement element, string? enclosingNamespace)
    {
        var name = ReadName(element, enclosingNamespace);
        string? doc = null;
        IEnumerable<SchemaName>? aliases = null;
        int? size = null;
        Dictionary<string, JsonElement>? properties = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type"u8) || property.NameEquals("name"u8) || property.NameEquals("namespace"u8))
            {
                continue;
            }

            if (property.NameEquals("doc"u8))
            {
                doc = ReadString(property);
            }
            else if (property.NameEquals("aliases"u8))
            {
                aliases = ReadAliases(property, name.Namespace);
            }
            else if (property.NameEquals("size"u8))
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) || value < 0)
                {
                    Push("size");
                    throw Error($"The 'size' of fixed '{name.FullName}' must be a non-negative integer.");
                }

                size = value;
            }
            else
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (size is not { } fixedSize)
        {
            throw Error($"Fixed '{name.FullName}' must have a 'size'.");
        }

        var logicalType = properties is null ? null : TakeLogicalType(properties, AvroSchemaType.Fixed, fixedSize);
        FixedSchema schema;
        try
        {
            schema = new FixedSchema(name, fixedSize, logicalType, doc, aliases, Keep(properties));
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Register(schema);
        return schema;
    }

    private ArraySchema ReadArray(JsonElement element, string? enclosingNamespace)
    {
        JsonElement items = default;
        Dictionary<string, JsonElement>? properties = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("items"u8))
            {
                items = property.Value;
            }
            else if (!property.NameEquals("type"u8))
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (items.ValueKind == JsonValueKind.Undefined)
        {
            throw Error("An array schema must have an 'items' attribute.");
        }

        Push("items");
        var itemSchema = ReadSchema(items, enclosingNamespace);
        Pop();
        return new ArraySchema(itemSchema, Keep(properties));
    }

    private MapSchema ReadMap(JsonElement element, string? enclosingNamespace)
    {
        JsonElement values = default;
        Dictionary<string, JsonElement>? properties = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("values"u8))
            {
                values = property.Value;
            }
            else if (!property.NameEquals("type"u8))
            {
                (properties ??= new(StringComparer.Ordinal))[property.Name] = property.Value;
            }
        }

        if (values.ValueKind == JsonValueKind.Undefined)
        {
            throw Error("A map schema must have a 'values' attribute.");
        }

        Push("values");
        var valueSchema = ReadSchema(values, enclosingNamespace);
        Pop();
        return new MapSchema(valueSchema, Keep(properties));
    }

    private UnionSchema ReadUnion(JsonElement element, string? enclosingNamespace)
    {
        var branches = new AvroSchema[element.GetArrayLength()];
        var index = 0;
        foreach (var branch in element.EnumerateArray())
        {
            PushIndex(index);
            branches[index++] = ReadSchema(branch, enclosingNamespace);
            Pop();
        }

        try
        {
            return new UnionSchema(branches);
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }
    }

    private SchemaName ReadName(JsonElement element, string? enclosingNamespace)
    {
        if (!element.TryGetProperty("name"u8, out var nameElement))
        {
            throw Error("A named schema must have a 'name'.");
        }

        Push("name");
        if (nameElement.ValueKind != JsonValueKind.String)
        {
            throw Error("The 'name' attribute must be a string.");
        }

        var @namespace = enclosingNamespace;
        if (element.TryGetProperty("namespace"u8, out var namespaceElement))
        {
            switch (namespaceElement.ValueKind)
            {
                case JsonValueKind.String:
                    @namespace = namespaceElement.GetString();
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    Pop();
                    Push("namespace");
                    throw Error("The 'namespace' attribute must be a string.");
            }
        }

        var simpleName = nameElement.GetString()!;

        // A dotted name ignores the namespace attribute; otherwise a bad namespace is reported where it is written.
        if (_options.ValidateNames && namespaceElement.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(@namespace)
            && simpleName.AsSpan().IndexOf('.') < 0 && !AvroNames.IsValidNamespace(@namespace.AsSpan()))
        {
            Pop();
            Push("namespace");
            throw Error($"'{@namespace}' is not a valid Avro namespace.");
        }

        SchemaName name;
        try
        {
            name = new SchemaName(simpleName, @namespace, _options.ValidateNames);
        }
        catch (AvroSchemaException ex)
        {
            throw Error(ex.Message, ex);
        }

        Pop();
        return name;
    }

    private SchemaName[] ReadAliases(JsonProperty property, string? @namespace)
    {
        var aliases = ReadStringArray(property);
        var names = new SchemaName[aliases.Length];
        Push(property.Name);
        for (var i = 0; i < aliases.Length; i++)
        {
            PushIndex(i);
            try
            {
                names[i] = new SchemaName(aliases[i], @namespace, _options.ValidateNames);
            }
            catch (AvroSchemaException ex)
            {
                throw Error(ex.Message, ex);
            }

            Pop();
        }

        Pop();
        return names;
    }

    private string ReadString(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            Push(property.Name);
            throw Error($"The '{property.Name}' attribute must be a string.");
        }

        return property.Value.GetString()!;
    }

    private string[] ReadStringArray(JsonProperty property)
    {
        var value = property.Value;
        if (value.ValueKind != JsonValueKind.Array)
        {
            Push(property.Name);
            throw Error($"The '{property.Name}' attribute must be an array of strings.");
        }

        var result = new string[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                Push(property.Name);
                PushIndex(i);
                throw Error($"The '{property.Name}' attribute must be an array of strings.");
            }

            result[i++] = item.GetString()!;
        }

        return result;
    }

    private FieldOrder ReadOrder(JsonProperty property)
    {
        var value = property.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.ValueEquals("ascending"u8))
            {
                return FieldOrder.Ascending;
            }

            if (value.ValueEquals("descending"u8))
            {
                return FieldOrder.Descending;
            }

            if (value.ValueEquals("ignore"u8))
            {
                return FieldOrder.Ignore;
            }
        }

        Push("order");
        throw Error("The 'order' attribute must be \"ascending\", \"descending\" or \"ignore\".");
    }

    /// <summary>
    /// Removes the attributes of a valid logical type from <paramref name="properties"/> and returns it.
    /// Unknown or invalid logical types are left in place, as the specification requires them to be ignored.
    /// </summary>
    /// <summary>
    /// Copies the custom properties a schema keeps out of the parsed document, which is pooled and returned when
    /// parsing ends. Only what is kept is copied: the logical type's own attributes have been taken out by then.
    /// </summary>
    private static Dictionary<string, JsonElement>? Keep(Dictionary<string, JsonElement>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return properties;
        }

        var names = new string[properties.Count];
        properties.Keys.CopyTo(names, 0);
        foreach (var name in names)
        {
            properties[name] = properties[name].Clone();
        }

        return properties;
    }

    private static AvroLogicalType? TakeLogicalType(Dictionary<string, JsonElement> properties, AvroSchemaType type, int fixedSize)
    {
        if (!properties.TryGetValue("logicalType", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        AvroLogicalType? logicalType;
        var isDecimal = element.ValueEquals("decimal"u8);
        if (isDecimal)
        {
            logicalType = TryReadDecimal(properties, type, fixedSize);
        }
        else
        {
            logicalType = AvroLogicalType.FromName(element.GetString()!);
            if (logicalType is not null && !IsValidTarget(logicalType, type, fixedSize))
            {
                logicalType = null;
            }
        }

        if (logicalType is null)
        {
            return null;
        }

        properties.Remove("logicalType");
        if (isDecimal)
        {
            properties.Remove("precision");
            properties.Remove("scale");
        }

        return logicalType;
    }

    private static DecimalLogicalType? TryReadDecimal(Dictionary<string, JsonElement> properties, AvroSchemaType type, int fixedSize)
    {
        if (type is not (AvroSchemaType.Bytes or AvroSchemaType.Fixed))
        {
            return null;
        }

        if (!properties.TryGetValue("precision", out var precisionElement)
            || precisionElement.ValueKind != JsonValueKind.Number
            || !precisionElement.TryGetInt32(out var precision)
            || precision <= 0)
        {
            return null;
        }

        var scale = 0;
        if (properties.TryGetValue("scale", out var scaleElement)
            && (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetInt32(out scale)))
        {
            return null;
        }

        if (scale < 0 || scale > precision)
        {
            return null;
        }

        if (type == AvroSchemaType.Fixed && precision > DecimalLogicalType.MaxPrecisionForFixedSize(fixedSize))
        {
            return null;
        }

        return new DecimalLogicalType(precision, scale);
    }

    private static bool IsValidTarget(AvroLogicalType logicalType, AvroSchemaType type, int fixedSize) => logicalType.Kind switch
    {
        AvroLogicalTypeKind.BigDecimal => type == AvroSchemaType.Bytes,
        AvroLogicalTypeKind.Uuid => type == AvroSchemaType.String || (type == AvroSchemaType.Fixed && fixedSize == 16),
        AvroLogicalTypeKind.Date or AvroLogicalTypeKind.TimeMillis => type == AvroSchemaType.Int,
        AvroLogicalTypeKind.Duration => type == AvroSchemaType.Fixed && fixedSize == 12,
        _ => type == AvroSchemaType.Long,
    };

    private void Register(NamedSchema schema)
    {
        if (_pending.ContainsKey(schema.FullName))
        {
            throw Error($"The name '{schema.FullName}' is already defined.");
        }

        if (_committed.TryGetValue(schema.FullName, out var existing))
        {
            if (!_options.AllowIdenticalRedefinitions)
            {
                throw Error($"The name '{schema.FullName}' is already defined.");
            }

            // Checked against the existing definition once this schema is complete (see Read).
            _redefinitions.Add((schema, existing, KeepPath()));
        }

        _pending.Add(schema.FullName, schema);
    }

    private void Push(string property) => _path.Add(JsonPathSegment.ForProperty(property));

    private void PushIndex(int index) => _path.Add(JsonPathSegment.ForIndex(index));

    private void Pop()
    {
        _path.RemoveAt(_path.Count - 1);
        if (_pathNodes.Count > _path.Count)
        {
            _pathNodes.RemoveAt(_pathNodes.Count - 1);
        }
    }

    // Keeps the current path for an error found later. Copying it for each default made parsing quadratic in the
    // nesting depth (#129); the nodes cost only the segments pushed since the last kept path.
    private PathNode? KeepPath()
    {
        for (var i = _pathNodes.Count; i < _path.Count; i++)
        {
            _pathNodes.Add(new PathNode(i == 0 ? null : _pathNodes[i - 1], _path[i]));
        }

        return _path.Count == 0 ? null : _pathNodes[_path.Count - 1];
    }

    private ParseError Error(string message, Exception? inner = null) => new(message, [.. _path], inner);

    private struct FieldAttributes
    {
        public string? Name;
        public JsonElement Type;
        public JsonElement? Default;
        public string? Doc;
        public FieldOrder Order;
        public string[]? Aliases;
        public Dictionary<string, JsonElement>? Properties;
    }

    private readonly record struct PendingDefault(string FieldName, AvroSchema Schema, JsonElement Value, PathNode? Path);

    /// <summary>A kept JSON path: its last segment, and the path before it.</summary>
    private sealed class PathNode(PathNode? parent, JsonPathSegment segment)
    {
        public PathNode? Parent { get; } = parent;

        public JsonPathSegment Segment { get; } = segment;

        public static JsonPathSegment[] ToArray(PathNode? path)
        {
            var length = 0;
            for (var node = path; node is not null; node = node.Parent)
            {
                length++;
            }

            var segments = new JsonPathSegment[length];
            for (var node = path; node is not null; node = node.Parent)
            {
                segments[--length] = node.Segment;
            }

            return segments;
        }
    }

    /// <summary>An error with the JSON path where it occurred; converted to <see cref="AvroSchemaException"/> by the parser.</summary>
#pragma warning disable CA1064, CA1032, RCS1194 // Internal control-flow exception, never escapes the parser.
    internal sealed class ParseError(string reason, JsonPathSegment[] path, Exception? inner = null) : Exception(reason, inner)
#pragma warning restore CA1064, CA1032, RCS1194
    {
        public string Reason { get; } = reason;

        public JsonPathSegment[] Path { get; } = path;
    }
}
