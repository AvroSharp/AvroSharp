using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>
/// A named schema: a <see cref="RecordSchema">record</see>, <see cref="EnumSchema">enum</see> or
/// <see cref="FixedSchema">fixed</see>.
/// </summary>
public abstract class NamedSchema : AvroSchema
{
    private protected NamedSchema(
        AvroSchemaType type,
        SchemaName name,
        IEnumerable<SchemaName>? aliases,
        string? doc,
        AvroLogicalType? logicalType,
        IReadOnlyDictionary<string, JsonElement>? properties)
        : base(type, logicalType, properties)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Aliases = aliases?.ToArray() ?? [];
        Doc = doc;
    }

    /// <summary>Gets the name of the schema.</summary>
    public SchemaName Name { get; }

    /// <summary>Gets the full name (<c>namespace.name</c>).</summary>
    public string FullName => Name.FullName;

    /// <summary>Gets the alternative names, resolved against this schema's namespace.</summary>
    public IReadOnlyList<SchemaName> Aliases { get; }

    /// <summary>Gets the documentation, or <see langword="null"/>.</summary>
    public string? Doc { get; }
}
