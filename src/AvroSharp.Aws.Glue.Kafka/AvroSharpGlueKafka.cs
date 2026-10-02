using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Amazon.Glue;
using AvroSharp.Generic;
using Confluent.Kafka;

namespace AvroSharp.Aws.Glue.Kafka;

/// <summary>
/// A Confluent.Kafka serializer in AWS Glue Schema Registry's wire format, on <see cref="AvroSharpGlueSerializer"/>,
/// as AWS's <c>GlueSchemaRegistryKafkaSerializer</c>. The schema is named after the topic unless the options name it.
/// </summary>
/// <typeparam name="T">The type written: a type <see cref="Serialization.AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
/// <remarks>
/// It is both asynchronous and synchronous: Confluent.Kafka's producer builder has a <c>SetValueSerializer</c> for
/// each, so pass it as one of them (<c>(IAsyncSerializer&lt;T&gt;)serializer</c>), or use
/// <see cref="AvroSharpGlueKafkaExtensions"/>. The synchronous one waits for Glue the first time each schema is
/// written.
/// </remarks>
public sealed class AvroSharpGlueKafkaSerializer<T> : IAsyncSerializer<T>, ISerializer<T>
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

    /// <summary>Writes a value. A <see langword="null"/> value, or a null <see cref="AvroValue"/>, is a tombstone: no message body.</summary>
    /// <param name="data">The value.</param>
    /// <param name="context">The topic.</param>
    public async Task<byte[]?> SerializeAsync(T data, SerializationContext context) =>
        IsTombstone(data) ? null : await _serializer.SerializeAsync(data, context.Topic, context.Component == MessageComponentType.Key).ConfigureAwait(false);

    /// <summary>Writes a value, waiting for Glue when the schema's version isn't known yet. A <see langword="null"/> value, or a null <see cref="AvroValue"/>, is a tombstone: no message body.</summary>
    /// <param name="data">The value.</param>
    /// <param name="context">The topic.</param>
    public byte[]? Serialize(T data, SerializationContext context) =>
        IsTombstone(data) ? null : SyncWait.Result(_serializer.SerializeAsync(data, context.Topic, context.Component == MessageComponentType.Key));

    private static bool IsTombstone(T data) => data is null || data is AvroValue { IsNull: true };
}

/// <summary>
/// A Confluent.Kafka deserializer of AWS Glue Schema Registry's wire format, on <see cref="AvroSharpGlueSerializer"/>,
/// as AWS's <c>GlueSchemaRegistryKafkaDeserializer</c>.
/// </summary>
/// <typeparam name="T">The type read: a type <see cref="Serialization.AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
/// <remarks>
/// It is both asynchronous and synchronous. Confluent.Kafka's consumer takes synchronous deserializers, which wait
/// for Glue the first time each schema version is read.
/// </remarks>
public sealed class AvroSharpGlueKafkaDeserializer<T> : IAsyncDeserializer<T>, IDeserializer<T>
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
    public async Task<T> DeserializeAsync(ReadOnlyMemory<byte> data, bool isNull, SerializationContext context) =>
        isNull ? Tombstone() : await _serializer.DeserializeAsync<T>(data).ConfigureAwait(false);

    /// <summary>Reads a value, waiting for Glue when its schema version isn't known yet. A tombstone reads as <see langword="null"/>; it can't be read as a value type.</summary>
    /// <param name="data">The message body.</param>
    /// <param name="isNull">Whether the message has no body.</param>
    /// <param name="context">The topic.</param>
    /// <exception cref="InvalidOperationException">The message is a tombstone and <typeparamref name="T"/> is a value type.</exception>
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) =>
        isNull ? Tombstone() : SyncWait.Result(_serializer.DeserializeAsync<T>(data.ToArray()));

    private static T Tombstone() =>
        default(T) is null || typeof(T) == typeof(AvroValue)
            ? default!
            : throw new InvalidOperationException($"The message has no body (a tombstone), which can't be read as the value type {typeof(T).Name}.");
}

/// <summary>Sets AvroSharp's Glue Schema Registry serializers and deserializers on Confluent.Kafka's producer and consumer builders.</summary>
public static class AvroSharpGlueKafkaExtensions
{
    /// <summary>Writes values with <see cref="AvroSharpGlueKafkaSerializer{T}"/>, as its synchronous interface, so both <c>Produce</c> and <c>ProduceAsync</c> work. It waits for Glue the first time each schema is written.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="glue">The Glue client.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpGlueValueSerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, IAmazonGlue glue, AvroSharpGlueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer((ISerializer<TValue>)new AvroSharpGlueKafkaSerializer<TValue>(glue, options));
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
        return builder.SetValueDeserializer(new AvroSharpGlueKafkaDeserializer<TValue>(glue, options));
    }
}

/// <summary>Waits for a value task, without a task allocation when it has already completed, as a cached call does.</summary>
internal static class SyncWait
{
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "Confluent.Kafka's synchronous interfaces; the AWS SDK for .NET calls Glue asynchronously only, and once per schema version.")]
    public static TResult Result<TResult>(ValueTask<TResult> task) =>
        task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
}
