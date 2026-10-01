using System;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>Sets AvroSharp serializers and deserializers on Confluent.Kafka's producer and consumer builders.</summary>
public static class AvroSharpSerdeExtensions
{
    /// <summary>Writes keys with an <see cref="AvroSharpSerializer{T}"/> of <typeparamref name="TKey"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpKeySerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpSerializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetKeySerializer(new AvroSharpSerializer<TKey>(client, config));
    }

    /// <summary>Writes values with an <see cref="AvroSharpSerializer{T}"/> of <typeparamref name="TValue"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpValueSerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpSerializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer(new AvroSharpSerializer<TValue>(client, config));
    }

    /// <summary>
    /// Reads keys with an <see cref="AvroSharpDeserializer{T}"/> of <typeparamref name="TKey"/>, which
    /// <see cref="AvroTypes"/> knows. The consumer calls deserializers synchronously, so the registry is called
    /// synchronously the first time each schema ID is seen.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static ConsumerBuilder<TKey, TValue> SetAvroSharpKeyDeserializer<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetKeyDeserializer(new AvroSharpDeserializer<TKey>(client, config).AsSyncOverAsync());
    }

    /// <summary>
    /// Reads values with an <see cref="AvroSharpDeserializer{T}"/> of <typeparamref name="TValue"/>, which
    /// <see cref="AvroTypes"/> knows. The consumer calls deserializers synchronously, so the registry is called
    /// synchronously the first time each schema ID is seen.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static ConsumerBuilder<TKey, TValue> SetAvroSharpValueDeserializer<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueDeserializer(new AvroSharpDeserializer<TValue>(client, config).AsSyncOverAsync());
    }
}
