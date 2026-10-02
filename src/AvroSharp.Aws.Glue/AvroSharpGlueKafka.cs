using System;
using System.Threading.Tasks;
using Amazon.Glue;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;

namespace AvroSharp.Aws.Glue;

/// <summary>
/// A Confluent.Kafka serializer in AWS Glue Schema Registry's wire format, on <see cref="AvroSharpGlueSerializer"/>,
/// as AWS's <c>GlueSchemaRegistryKafkaSerializer</c>. The schema is named after the topic unless the options name it.
/// </summary>
/// <typeparam name="T">The type written: a type <see cref="Serialization.AvroTypes"/> knows, <see cref="Generic.GenericRecord"/> or <see cref="Generic.AvroValue"/>.</typeparam>
public sealed class AvroSharpGlueKafkaSerializer<T> : IAsyncSerializer<T>
{
    private readonly AvroSharpGlueSerializer _serializer;

    /// <summary>Creates a serializer.</summary>
    /// <param name="glue">The Glue client.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public AvroSharpGlueKafkaSerializer(IAmazonGlue glue, AvroSharpGlueOptions? options = null)
        : this(new AvroSharpGlueSerializer(glue, options))
    {
    }

    /// <summary>Creates a serializer on <paramref name="serializer"/>, sharing its caches.</summary>
    /// <param name="serializer">The serializer.</param>
    public AvroSharpGlueKafkaSerializer(AvroSharpGlueSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
    }

    /// <summary>Writes a value. A <see langword="null"/> value is a tombstone: no message body.</summary>
    /// <param name="data">The value.</param>
    /// <param name="context">The topic.</param>
    public async Task<byte[]> SerializeAsync(T data, SerializationContext context) =>
        data is null ? null! : await _serializer.SerializeAsync(data, context.Topic).ConfigureAwait(false);
}

/// <summary>
/// A Confluent.Kafka deserializer of AWS Glue Schema Registry's wire format, on <see cref="AvroSharpGlueSerializer"/>,
/// as AWS's <c>GlueSchemaRegistryKafkaDeserializer</c>.
/// </summary>
/// <typeparam name="T">The type read: a type <see cref="Serialization.AvroTypes"/> knows, <see cref="Generic.GenericRecord"/> or <see cref="Generic.AvroValue"/>.</typeparam>
/// <remarks>Confluent.Kafka's consumer takes synchronous deserializers: use <c>.AsSyncOverAsync()</c>, or <see cref="AvroSharpGlueKafkaExtensions"/>.</remarks>
public sealed class AvroSharpGlueKafkaDeserializer<T> : IAsyncDeserializer<T>
{
    private readonly AvroSharpGlueSerializer _serializer;

    /// <summary>Creates a deserializer.</summary>
    /// <param name="glue">The Glue client.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public AvroSharpGlueKafkaDeserializer(IAmazonGlue glue, AvroSharpGlueOptions? options = null)
        : this(new AvroSharpGlueSerializer(glue, options))
    {
    }

    /// <summary>Creates a deserializer on <paramref name="serializer"/>, sharing its caches.</summary>
    /// <param name="serializer">The serializer.</param>
    public AvroSharpGlueKafkaDeserializer(AvroSharpGlueSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
    }

    /// <summary>Reads a value. A tombstone reads as <see langword="null"/>; it can't be read as a value type.</summary>
    /// <param name="data">The message body.</param>
    /// <param name="isNull">Whether the message has no body.</param>
    /// <param name="context">The topic.</param>
    /// <exception cref="InvalidOperationException">The message is a tombstone and <typeparamref name="T"/> is a value type.</exception>
    public async Task<T> DeserializeAsync(ReadOnlyMemory<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
        {
            return default(T) is null || typeof(T) == typeof(Generic.AvroValue)
                ? default!
                : throw new InvalidOperationException($"The message has no body (a tombstone), which can't be read as the value type {typeof(T).Name}.");
        }

        return await _serializer.DeserializeAsync<T>(data).ConfigureAwait(false);
    }
}

/// <summary>Sets AvroSharp's Glue Schema Registry serializers and deserializers on Confluent.Kafka's producer and consumer builders.</summary>
public static class AvroSharpGlueKafkaExtensions
{
    /// <summary>Writes values with <see cref="AvroSharpGlueKafkaSerializer{T}"/>.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="glue">The Glue client.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpGlueValueSerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, IAmazonGlue glue, AvroSharpGlueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer(new AvroSharpGlueKafkaSerializer<TValue>(glue, options));
    }

    /// <summary>
    /// Reads values with <see cref="AvroSharpGlueKafkaDeserializer{T}"/>. The consumer calls deserializers
    /// synchronously, so it waits for the registry the first time each schema version is seen.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="glue">The Glue client.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public static ConsumerBuilder<TKey, TValue> SetAvroSharpGlueValueDeserializer<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder, IAmazonGlue glue, AvroSharpGlueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueDeserializer(new AvroSharpGlueKafkaDeserializer<TValue>(glue, options).AsSyncOverAsync());
    }
}
