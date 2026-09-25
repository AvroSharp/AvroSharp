using System;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>A fixed value in the generic data model: a schema and exactly <see cref="FixedSchema.Size"/> bytes.</summary>
public sealed class GenericFixed : IEquatable<GenericFixed>
{
    private readonly byte[] _bytes;

    /// <summary>Initializes a fixed value. The array is not copied.</summary>
    /// <param name="schema">The fixed schema.</param>
    /// <param name="bytes">The bytes; the length must equal the schema's size.</param>
    /// <exception cref="ArgumentException">The length does not match the schema.</exception>
    public GenericFixed(FixedSchema schema, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != schema.Size)
        {
            throw new ArgumentException($"Fixed '{schema.FullName}' needs {schema.Size} bytes, but {bytes.Length} were given.", nameof(bytes));
        }

        Schema = schema;
        _bytes = bytes;
    }

    /// <summary>Gets the fixed schema.</summary>
    public FixedSchema Schema { get; }

    /// <summary>Gets the bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Gets the bytes as the underlying array (not a copy).</summary>
    public byte[] GetBytesUnsafe() => _bytes;

    /// <summary>Compares schema full names and bytes.</summary>
    /// <param name="other">The other value.</param>
    public bool Equals(GenericFixed? other) =>
        other is not null && Schema.Name == other.Schema.Name && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GenericFixed other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Schema.Name.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Schema.FullName + " " + Convert.ToHexString(_bytes);
}
