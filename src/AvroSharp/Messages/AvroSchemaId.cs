using System;
using System.Globalization;

namespace AvroSharp.Messages;

/// <summary>
/// A schema ID as a schema registry assigns it: a number (Confluent, Apicurio) or a GUID (Confluent's version 1
/// framing, AWS Glue's schema version ID).
/// </summary>
/// <seealso cref="AvroRegistryFraming"/>
/// <seealso cref="IAvroSchemaIdResolver"/>
public readonly struct AvroSchemaId : IEquatable<AvroSchemaId>
{
    private readonly long _number;
    private readonly Guid _guid;

    private AvroSchemaId(long number, Guid guid, bool isGuid)
    {
        _number = number;
        _guid = guid;
        IsGuid = isGuid;
    }

    /// <summary>Gets whether the ID is a GUID; otherwise it is a number.</summary>
    public bool IsGuid { get; }

    /// <summary>Gets the numeric ID.</summary>
    /// <exception cref="InvalidOperationException">The ID is a GUID.</exception>
    public long Number => !IsGuid ? _number : throw new InvalidOperationException("The schema ID is a GUID, not a number.");

    /// <summary>Gets the GUID.</summary>
    /// <exception cref="InvalidOperationException">The ID is a number.</exception>
    public Guid Guid => IsGuid ? _guid : throw new InvalidOperationException("The schema ID is a number, not a GUID.");

    /// <summary>Compares two IDs.</summary>
    /// <param name="left">The first ID.</param>
    /// <param name="right">The second ID.</param>
    public static bool operator ==(AvroSchemaId left, AvroSchemaId right) => left.Equals(right);

    /// <summary>Compares two IDs.</summary>
    /// <param name="left">The first ID.</param>
    /// <param name="right">The second ID.</param>
    public static bool operator !=(AvroSchemaId left, AvroSchemaId right) => !left.Equals(right);

    /// <summary>Creates a numeric ID.</summary>
    /// <param name="id">The ID.</param>
    public static AvroSchemaId FromNumber(long id) => new(id, Guid.Empty, isGuid: false);

    /// <summary>Creates a GUID ID.</summary>
    /// <param name="id">The ID.</param>
    public static AvroSchemaId FromGuid(Guid id) => new(0, id, isGuid: true);

    /// <inheritdoc/>
    public bool Equals(AvroSchemaId other) => IsGuid == other.IsGuid && _number == other._number && _guid == other._guid;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AvroSchemaId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => IsGuid ? _guid.GetHashCode() : _number.GetHashCode();

    /// <summary>Returns the number, or the GUID in its 8-4-4-4-12 form.</summary>
    public override string ToString() => IsGuid ? _guid.ToString("D") : _number.ToString(CultureInfo.InvariantCulture);
}
