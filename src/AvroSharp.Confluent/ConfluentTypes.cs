using System.Text.Json.Nodes;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.Kafka;

namespace AvroSharp.Confluent;

/// <summary>The types this package knows besides <see cref="AvroTypes"/>: Confluent.Kafka's <see cref="Null"/>.</summary>
internal static class ConfluentTypes
{
    // Confluent.Kafka's Null, as Confluent's serde maps it: the schema "null", whose value has no bytes.
    private static readonly AvroTypeInfo<Null> NullInfo = new(() => AvroSchema.Null, (ref _, _) => { }, (ref _) => null!);

    /// <summary>The type's info: <see cref="Null"/>'s, or the one <see cref="AvroTypes"/> has.</summary>
    public static AvroTypeInfo<T> Get<T>() =>
        typeof(T) == typeof(Null) ? (AvroTypeInfo<T>)(object)NullInfo : AvroTypes.Get<T>();

    /// <summary>
    /// The schema's JSON without the property <c>avro.java.string</c>, anywhere in it, as Java's serializer removes it
    /// with <c>avro.remove.java.properties</c>: so the same <c>.avsc</c> registers as one version from Java and .NET.
    /// </summary>
    public static string RemoveJavaProperties(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson);
        return Remove(node) ? node!.ToJsonString() : schemaJson;
    }

    private static bool Remove(JsonNode? node)
    {
        var removed = false;
        switch (node)
        {
            case JsonObject obj:
                removed = obj.Remove("avro.java.string");
                foreach (var item in obj)
                {
                    removed |= Remove(item.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    removed |= Remove(item);
                }

                break;
        }

        return removed;
    }
}
