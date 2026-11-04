namespace AvroSharp.Confluent;

/// <summary>
/// How a serializer or deserializer treats tombstones: messages with no body
/// (<c>avro.serializer.tombstone.behavior</c> and <c>avro.deserializer.tombstone.behavior</c>, AvroSharp's own keys).
/// </summary>
public enum TombstoneBehavior
{
    /// <summary>
    /// Confluent's behavior, the default. A <see langword="null"/> value (a null <see cref="Generic.AvroValue"/> too, unless
    /// the schema is <c>"null"</c>) is written as a tombstone, and reading a tombstone into a value type throws.
    /// </summary>
    None,

    /// <summary>
    /// A tombstone can't be confused with a value. When the serializer or deserializer is created, the type must be able
    /// to hold <see langword="null"/> (a reference type, <see cref="System.Nullable{T}"/> or <see cref="Generic.AvroValue"/>),
    /// and the schema must not hold null (the schema <c>"null"</c>, or a union with a <c>"null"</c> branch); the
    /// deserializer checks each writer's schema too. Keys have no tombstones: a <see langword="null"/> key, or a key with
    /// no body, throws.
    /// </summary>
    Strict,
}
