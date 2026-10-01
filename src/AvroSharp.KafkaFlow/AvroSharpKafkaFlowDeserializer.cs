using System;
using System.IO;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using global::KafkaFlow;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// KafkaFlow's <see cref="IDeserializer"/> on <see cref="AvroSharpDeserializer{T}"/>: reads each message as the type
/// KafkaFlow's type resolver picked, from the writer's schema (fetched by the message's schema ID) resolved to the
/// type's, with one deserializer per type.
/// </summary>
/// <remarks>Usually added with <see cref="AvroSharpKafkaFlowExtensions"/>' <c>AddSchemaRegistryAvroSharpDeserializer</c> methods.</remarks>
public sealed class AvroSharpKafkaFlowDeserializer : IDeserializer
{
    private readonly DeserializersByType _deserializers;

    /// <summary>Creates a deserializer.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public AvroSharpKafkaFlowDeserializer(ISchemaRegistryClient client, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _deserializers = new DeserializersByType(client, config);
    }

    /// <summary>Reads a message as <paramref name="type"/>.</summary>
    /// <param name="input">The message body.</param>
    /// <param name="type">The type to read, which <see cref="Serialization.AvroTypes"/> knows.</param>
    /// <param name="context">The topic.</param>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is not one <see cref="Serialization.AvroTypes"/> knows.</exception>
    public async Task<object> DeserializeAsync(Stream input, Type type, ISerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(context);
        return await _deserializers.For(type)
            .DeserializeAsync(await BodyAsync(input).ConfigureAwait(false), isNull: false, new SerializationContext(MessageComponentType.Value, context.Topic))
            .ConfigureAwait(false);
    }

    // KafkaFlow's middleware passes a MemoryStream over the message, whose buffer it doesn't expose: the body is read
    // into one array of its length.
    private static async Task<ReadOnlyMemory<byte>> BodyAsync(Stream input)
    {
        if (input is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            return buffer.AsMemory((int)memory.Position);
        }

        if (input.CanSeek)
        {
            var body = new byte[input.Length - input.Position];
            var read = 0;
            while (read < body.Length)
            {
                var n = await input.ReadAsync(body.AsMemory(read)).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return body.AsMemory(0, read);
        }

        using var copy = new MemoryStream();
        await input.CopyToAsync(copy).ConfigureAwait(false);
        return copy.GetBuffer().AsMemory(0, (int)copy.Length);
    }
}
