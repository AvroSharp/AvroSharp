using System;
using System.Runtime.CompilerServices;
using AvroSharp.Schemas;

namespace AvroSharp.Aws.Glue;

/// <summary>
/// What <see cref="AvroSharpGlueOptions.SchemaNameStrategy"/> names a schema from, as AWS's
/// <c>AWSSchemaNamingStrategy.getSchemaName(transportName, data, isKey)</c>.
/// </summary>
public readonly struct AvroSharpGlueSchemaNameContext : IEquatable<AvroSharpGlueSchemaNameContext>
{
    /// <summary>Initializes a new instance of the <see cref="AvroSharpGlueSchemaNameContext"/> struct.</summary>
    /// <param name="transportName">The Kafka topic or Kinesis stream.</param>
    /// <param name="schema">The schema of the key or value written.</param>
    /// <param name="isKey">Whether a message key is written.</param>
    public AvroSharpGlueSchemaNameContext(string transportName, AvroSchema schema, bool isKey)
    {
        ArgumentNullException.ThrowIfNull(transportName);
        ArgumentNullException.ThrowIfNull(schema);
        TransportName = transportName;
        Schema = schema;
        IsKey = isKey;
    }

    /// <summary>Gets the Kafka topic or Kinesis stream.</summary>
    public string TransportName { get; }

    /// <summary>Gets the schema of the key or value written.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets a value indicating whether a message key is written.</summary>
    public bool IsKey { get; }

    /// <summary>Compares two contexts.</summary>
    /// <param name="left">A context.</param>
    /// <param name="right">Another context.</param>
    public static bool operator ==(AvroSharpGlueSchemaNameContext left, AvroSharpGlueSchemaNameContext right) => left.Equals(right);

    /// <summary>Compares two contexts.</summary>
    /// <param name="left">A context.</param>
    /// <param name="right">Another context.</param>
    public static bool operator !=(AvroSharpGlueSchemaNameContext left, AvroSharpGlueSchemaNameContext right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(AvroSharpGlueSchemaNameContext other) =>
        string.Equals(TransportName, other.TransportName, StringComparison.Ordinal) && ReferenceEquals(Schema, other.Schema) && IsKey == other.IsKey;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AvroSharpGlueSchemaNameContext other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        (((StringComparer.Ordinal.GetHashCode(TransportName ?? string.Empty) * 397) ^ RuntimeHelpers.GetHashCode(Schema)) * 2) + (IsKey ? 1 : 0);
}
