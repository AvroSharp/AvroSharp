using System;

namespace AvroSharp.Schemas;

/// <summary>
/// The full name of a named schema (record, enum or fixed): a simple name and an optional namespace.
/// </summary>
/// <remarks>
/// Names are compared by <see cref="FullName"/>, ordinally. The empty namespace and a <see langword="null"/>
/// namespace are the same (the null namespace) and are both exposed as <see langword="null"/>.
/// </remarks>
public sealed class SchemaName : IEquatable<SchemaName>
{
    /// <summary>
    /// Initializes a new name. If <paramref name="name"/> contains a dot it is treated as a full name and
    /// <paramref name="namespace"/> is ignored, as the specification requires.
    /// </summary>
    /// <param name="name">A simple name, or a full name containing dots.</param>
    /// <param name="namespace">The namespace, used only when <paramref name="name"/> has no dot.</param>
    /// <exception cref="AvroSchemaException">The name or namespace is not valid.</exception>
    public SchemaName(string name, string? @namespace = null)
        : this(name, @namespace, validate: true)
    {
    }

    internal SchemaName(string name, string? @namespace, bool validate)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dot = name.AsSpan().LastIndexOf('.');
        if (dot == 0)
        {
            // ".foo": the null namespace may not be part of a dotted name.
            if (validate)
            {
                throw new AvroSchemaException($"'{name}' is not a valid Avro full name.");
            }

            Name = name[1..];
            FullName = Name;
        }
        else if (dot > 0)
        {
            Name = name[(dot + 1)..];
            Namespace = name[..dot];
            FullName = name;
        }
        else
        {
            Name = name;
            Namespace = string.IsNullOrEmpty(@namespace) ? null : @namespace;
            FullName = Namespace is null ? name : string.Concat(Namespace, ".", name);
        }

        if (validate)
        {
            if (!AvroNames.IsValidName(Name.AsSpan()))
            {
                throw new AvroSchemaException($"'{Name}' is not a valid Avro name: it must start with [A-Za-z_] and contain only [A-Za-z0-9_].");
            }

            if (Namespace is not null && !AvroNames.IsValidNamespace(Namespace.AsSpan()))
            {
                throw new AvroSchemaException($"'{Namespace}' is not a valid Avro namespace.");
            }
        }

        if (Namespace is null && AvroNames.IsPrimitiveTypeName(Name))
        {
            throw new AvroSchemaException($"'{Name}' is a primitive type name and cannot be used as the name of a named schema.");
        }
    }

    /// <summary>Gets the simple name (the part after the last dot).</summary>
    public string Name { get; }

    /// <summary>Gets the namespace, or <see langword="null"/> for the null namespace.</summary>
    public string? Namespace { get; }

    /// <summary>Gets the full name: <c>namespace.name</c>, or just the name in the null namespace.</summary>
    public string FullName { get; }

    /// <summary>Determines whether two names are equal.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    public static bool operator ==(SchemaName? left, SchemaName? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two names differ.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    public static bool operator !=(SchemaName? left, SchemaName? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(SchemaName? other) => other is not null && string.Equals(FullName, other.FullName, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SchemaName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(FullName);

    /// <inheritdoc />
    public override string ToString() => FullName;
}
