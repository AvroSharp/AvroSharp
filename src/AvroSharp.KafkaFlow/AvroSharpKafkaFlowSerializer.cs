using System;
using System.IO;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using global::KafkaFlow;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// KafkaFlow's <see cref="ISerializer"/> on <see cref="AvroSharpSerializer{T}"/>: writes each message, of any type
/// <see cref="Serialization.AvroTypes"/> knows, in Confluent Schema Registry framing, with one serializer per type. The bytes
/// are those of KafkaFlow's Confluent Avro serializer for the same schema and value.
/// </summary>
/// <remarks>Usually added with <see cref="AvroSharpKafkaFlowExtensions.AddSchemaRegistryAvroSharpSerializer"/>.</remarks>
public sealed class AvroSharpKafkaFlowSerializer : ISerializer
{
    private readonly SerializersByType _serializers;

    /// <summary>Creates a serializer.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="config"/> puts the schema ID in a header: KafkaFlow's serializer middleware gives serializers no headers.
    /// </exception>
    public AvroSharpKafkaFlowSerializer(ISchemaRegistryClient client, AvroSharpSerializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (config?.SchemaIdStrategy == SchemaIdSerializerStrategy.Header)
        {
            throw new ArgumentException("KafkaFlow's serializer middleware gives serializers no message headers, so the schema ID can't go in one.", nameof(config));
        }

        _serializers = new SerializersByType(client, config);
    }

    /// <summary>Writes a message: its schema ID, registered or looked up once per type and subject, then its Avro encoding.</summary>
    /// <param name="message">The message.</param>
    /// <param name="output">The stream the message body is written to.</param>
    /// <param name="context">The topic.</param>
    /// <exception cref="InvalidOperationException">The message's type is not one <see cref="Serialization.AvroTypes"/> knows.</exception>
    public async Task SerializeAsync(object message, Stream output, ISerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(context);
        var bytes = await _serializers.For(message.GetType())
            .SerializeAsync(message, new SerializationContext(MessageComponentType.Value, context.Topic))
            .ConfigureAwait(false);
        await output.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
    }
}
