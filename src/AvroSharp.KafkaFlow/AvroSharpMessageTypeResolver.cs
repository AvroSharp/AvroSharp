using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;
using global::KafkaFlow;
using global::KafkaFlow.Middlewares.Serializer.Resolvers;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// Picks the type a consumed message is read as from its writer's schema: the schema ID in the message, the schema
/// from the registry (once per ID), and the type of that record's full name. That way one topic can carry several
/// record types, as with the <c>TopicRecord</c> subject name strategy.
/// </summary>
/// <remarks>
/// The type comes from the message types given to the constructor, by each one's schema name. Without them, it is
/// found by its .NET full name in the loaded assemblies, as KafkaFlow's Confluent Avro deserializer does, which
/// works when the .NET names match the Avro names (as they do by default for generated types).
/// </remarks>
public sealed class AvroSharpMessageTypeResolver : IMessageTypeResolver
{
    private readonly ISchemaRegistryClient _client;
    private readonly Func<string, Type?> _typeOf;
    private readonly ConcurrentDictionary<int, Type> _types = new();

    /// <summary>Creates a resolver that picks among <paramref name="messageTypes"/>, by their records' full names.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="messageTypes">The types messages are read as: types <see cref="AvroTypes"/> knows, of record schemas.</param>
    /// <exception cref="ArgumentException">A type's schema isn't a record, or two types have records of the same name.</exception>
    /// <exception cref="InvalidOperationException">A type is not one <see cref="AvroTypes"/> knows.</exception>
    public AvroSharpMessageTypeResolver(ISchemaRegistryClient client, IEnumerable<Type> messageTypes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(messageTypes);
        _client = client;
        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in messageTypes)
        {
            var name = (MessageTypes.Info(type).Schema as RecordSchema)?.FullName
                ?? throw new ArgumentException($"The schema of {type} is not a record, so a message's schema can't name it.", nameof(messageTypes));
            if (byName.TryGetValue(name, out var other))
            {
                throw new ArgumentException($"{other} and {type} both have the record {name}.", nameof(messageTypes));
            }

            byName.Add(name, type);
        }

        _typeOf = name => byName.TryGetValue(name, out var type) ? type : null;
    }

    /// <summary>
    /// Creates a resolver that finds the type of a record's full name in the loaded assemblies, as KafkaFlow's
    /// Confluent Avro deserializer does.
    /// </summary>
    /// <param name="client">The schema registry.</param>
    [RequiresUnreferencedCode("The message types are found by name in the loaded assemblies, and trimming can remove them. Pass the message types to the other constructor instead.")]
    public AvroSharpMessageTypeResolver(ISchemaRegistryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _typeOf = LoadedType;
    }

    /// <summary>Gets the type of the message's writer schema.</summary>
    /// <param name="context">The consumed message, in Confluent Schema Registry framing.</param>
    /// <exception cref="InvalidOperationException">
    /// The message isn't in Confluent framing with the schema ID in front, its schema isn't a record, or no type has that record.
    /// </exception>
    public async ValueTask<Type> OnConsumeAsync(IMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Message.Value is not byte[] { Length: >= 5 } message || message[0] != 0)
        {
            throw new InvalidOperationException("The message is not in Confluent Schema Registry framing: a zero byte, then the 4-byte schema ID.");
        }

        var id = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1, 4));
        if (_types.TryGetValue(id, out var type))
        {
            return type;
        }

        var schema = await _client.GetSchemaAsync(id).ConfigureAwait(false);
        var name = RecordName(schema.SchemaString)
            ?? throw new InvalidOperationException($"The schema {id} is not a record, so it names no message type. Read such a topic with a deserializer of one type.");
        type = _typeOf(name)
            ?? throw new InvalidOperationException($"No message type has the record {name} (schema {id}).");
        return _types.GetOrAdd(id, type);
    }

    /// <summary>Does nothing: a produced message's type is its value's.</summary>
    /// <param name="context">The produced message.</param>
    public ValueTask OnProduceAsync(IMessageContext context) => default;

    // A record's full name: its name when that has a dot, or else its namespace and name.
    private static string? RecordName(string schemaJson)
    {
        using var json = JsonDocument.Parse(schemaJson);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not ("record" or "error")
            || !root.TryGetProperty("name", out var nameElement)
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        return name.Contains('.', StringComparison.Ordinal) || !root.TryGetProperty("namespace", out var ns) || ns.GetString() is not { Length: > 0 } space
            ? name
            : space + "." + name;
    }

    [RequiresUnreferencedCode("Finds types by name in the loaded assemblies.")]
    private static Type? LoadedType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(name, throwOnError: false) is { } type && AvroTypes.TryGet(type, out _))
            {
                return type;
            }
        }

        return null;
    }
}
