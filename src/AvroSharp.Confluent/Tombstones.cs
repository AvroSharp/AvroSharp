using System;
using System.Linq;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Confluent;

/// <summary>The checks of <see cref="TombstoneBehavior.Strict"/>.</summary>
internal static class Tombstones
{
    /// <summary>Whether <typeparamref name="T"/> can hold <see langword="null"/>, the value of a tombstone.</summary>
    public static bool CanHoldNull<T>() => default(T) is null || typeof(T) == typeof(AvroValue);

    /// <summary>The message for a type that can't hold <see langword="null"/>.</summary>
    public static string TypeMessage<T>(string serde) =>
        $"{serde}: with TombstoneBehavior.Strict, the type must be able to hold null (a tombstone), and {typeof(T).Name} is a value type. Use {typeof(T).Name}? or TombstoneBehavior.None.";

    /// <summary>Throws when a value of <paramref name="schema"/> can be null, so a null value can't be told from a tombstone.</summary>
    public static void CheckSchema(AvroSchema schema, string serde)
    {
        if (schema.Type == AvroSchemaType.Null
            || (schema is UnionSchema union && union.Branches.Any(b => b.Type == AvroSchemaType.Null)))
        {
            throw new InvalidOperationException($"{serde}: with TombstoneBehavior.Strict, the schema can't hold null, because a null value couldn't be told from a tombstone; the schema is {schema.CanonicalForm}.");
        }
    }

    /// <summary>The exception for a key with no value: a key has no tombstone.</summary>
    public static InvalidOperationException NullKey(string serde) =>
        new($"{serde}: with TombstoneBehavior.Strict, a key can't be null or have no body: tombstones are values only.");
}
