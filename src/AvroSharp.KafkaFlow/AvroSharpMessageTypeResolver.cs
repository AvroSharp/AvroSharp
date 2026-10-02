using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;
using global::KafkaFlow;
using global::KafkaFlow.Middlewares.Serializer.Resolvers;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// Picks the type a consumed message is read as from its writer's schema: the schema ID in the message, the schema
/// from the registry (once per ID), and the type of that record's full name. That way one topic can carry several
/// record types, as with the <c>TopicRecord</c> subject name strategy, or a top-level union of records under one
/// subject, where the message's union branch names the record.
/// </summary>
/// <remarks>
/// The type comes from the message types given to the constructor, by each one's schema name or aliases. Without
/// them, it is found by its .NET full name in the loaded assemblies, as KafkaFlow's Confluent Avro deserializer does,
/// which works when the .NET names match the Avro names (as they do by default for generated types).
/// </remarks>
public sealed class AvroSharpMessageTypeResolver : IMessageTypeResolver
{
    private readonly ISchemaRegistryClient _client;
    private readonly Func<string, Type?> _typeOf;

    // The record names of each schema ID: one for a record, one per branch for a top-level union (null for a branch
    // that isn't a record).
    private readonly ConcurrentDictionary<int, string?[]> _records = new();

    // Without a list of types: the type of each record name found in the loaded assemblies, or, for one that wasn't,
    // how many assemblies were loaded when it was looked for, so it's looked for again only once more have loaded.
    private readonly ConcurrentDictionary<string, (Type? Type, int Assemblies)> _loaded = new(StringComparer.Ordinal);

    /// <summary>Creates a resolver that picks among <paramref name="messageTypes"/>, by their records' full names and aliases.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="messageTypes">The types messages are read as: types <see cref="AvroTypes"/> knows, of record schemas.</param>
    /// <exception cref="ArgumentException">No types are given, a type's schema isn't a record, or two types have records of the same name or alias.</exception>
    /// <exception cref="InvalidOperationException">A type is not one <see cref="AvroTypes"/> knows.</exception>
    public AvroSharpMessageTypeResolver(ISchemaRegistryClient client, IEnumerable<Type> messageTypes)
        : this(client, ByRecordName(messageTypes))
    {
    }

    internal AvroSharpMessageTypeResolver(ISchemaRegistryClient client, Dictionary<string, Type> byRecordName)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _typeOf = name => byRecordName.TryGetValue(name, out var type) ? type : null;
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
    /// The message isn't in Confluent framing with the schema ID in front, its schema isn't Avro, it isn't a record
    /// (or a union whose message branch is a record), or no type has that record.
    /// </exception>
    public async ValueTask<Type> OnConsumeAsync(IMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Message.Value is not byte[] { Length: >= 5 } message || message[0] != 0)
        {
            throw new InvalidOperationException(
                "The message is not in Confluent Schema Registry framing: a zero byte, then the 4-byte schema ID. A schema ID " +
                "in a message header can't be read: KafkaFlow gives deserializers no headers.");
        }

        var id = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1, 4));
        if (!_records.TryGetValue(id, out var records))
        {
            records = _records.GetOrAdd(id, RecordNames(id, await _client.GetSchemaAsync(id).ConfigureAwait(false)));
        }

        var name = records.Length == 1 ? records[0] : UnionBranch(id, records, message);
        return _typeOf(name!) ?? throw new InvalidOperationException($"No message type has the record {name} (schema {id}).");
    }

    // The message types by their records' full names and aliases.
    internal static Dictionary<string, Type> ByRecordName(IEnumerable<Type> messageTypes)
    {
        ArgumentNullException.ThrowIfNull(messageTypes);
        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in messageTypes)
        {
            ArgumentNullException.ThrowIfNull(type, nameof(messageTypes));
            var record = MessageTypes.Info(type).Schema as RecordSchema
                ?? throw new ArgumentException($"The schema of {type} is not a record, so a message's schema can't name it.", nameof(messageTypes));
            foreach (var name in record.Aliases.Select(alias => alias.FullName).Prepend(record.FullName))
            {
                if (byName.TryGetValue(name, out var other))
                {
                    throw new ArgumentException($"{other} and {type} both have the record name or alias {name}.", nameof(messageTypes));
                }

                byName.Add(name, type);
            }
        }

        return byName.Count > 0
            ? byName
            : throw new ArgumentException("No message types are given: list the types the topic carries.", nameof(messageTypes));
    }

    /// <summary>Does nothing: a produced message's type is its value's.</summary>
    /// <param name="context">The produced message.</param>
    public ValueTask OnProduceAsync(IMessageContext context) => default;

    // The record names of a schema: the record's, or each branch's of a top-level union (null for a branch that isn't
    // a record). A union's branches are full names, as a top-level union has no namespace to inherit.
    private static string?[] RecordNames(int id, Schema schema)
    {
        if (schema.SchemaType != SchemaType.Avro)
        {
            throw new InvalidOperationException($"The schema {id} is a {schema.SchemaType} schema, not Avro.");
        }

        using var json = JsonDocument.Parse(schema.SchemaString);
        var root = json.RootElement;
        string?[] names = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Select(branch => branch.ValueKind == JsonValueKind.String ? Named(branch.GetString()!) : RecordName(branch)).ToArray()
            : [RecordName(root)];
        return names.Any(name => name is not null)
            ? names
            : throw new InvalidOperationException($"The schema {id} is not a record (or a union of records), so it names no message type. Read such a topic with a deserializer of one type.");

        // A branch that names a type: a record defined elsewhere, unless it's a primitive.
        static string? Named(string name) =>
            name is "null" or "boolean" or "int" or "long" or "float" or "double" or "bytes" or "string" ? null : name;
    }

    // The record of the union branch the message holds: its index follows the 5-byte prefix.
    private static string UnionBranch(int id, string?[] records, byte[] message)
    {
        var reader = new AvroReader(message.AsSpan(5));
        var index = reader.ReadLong();
        return index >= 0 && index < records.Length && records[index] is { } name
            ? name
            : throw new InvalidOperationException($"Branch {index} of the union schema {id} is not a record, so it names no message type.");
    }

    // A record's full name: its name when that has a dot, or else its namespace and name.
    private static string? RecordName(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not ("record" or "error")
            || !schema.TryGetProperty("name", out var nameElement)
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        return name.Contains('.', StringComparison.Ordinal) || !schema.TryGetProperty("namespace", out var ns) || ns.GetString() is not { Length: > 0 } space
            ? name
            : space + "." + name;
    }

    [RequiresUnreferencedCode("Finds types by name in the loaded assemblies.")]
    private Type? LoadedType(string name)
    {
        if (_loaded.TryGetValue(name, out var known) && known.Type is not null)
        {
            return known.Type;
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (known.Assemblies == assemblies.Length && _loaded.ContainsKey(name))
        {
            return null;
        }

        Type? found = null;
        foreach (var assembly in assemblies)
        {
            if (assembly.GetType(name, throwOnError: false) is { } type && AvroTypes.TryGet(type, out _))
            {
                found = type;
                break;
            }
        }

        _loaded[name] = (found, assemblies.Length);
        return found;
    }
}
