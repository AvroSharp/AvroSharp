using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Azure.Data.SchemaRegistry;
using Azure.Messaging;
using BinaryData = System.BinaryData;

namespace AvroSharp.Azure.SchemaRegistry;

/// <summary>
/// Writes and reads Avro messages with Azure Schema Registry, as Microsoft's <c>SchemaRegistryAvroSerializer</c>
/// (Microsoft.Azure.Data.SchemaRegistry.ApacheAvro) does, without Apache.Avro. The message is a
/// <see cref="MessageContent"/>, or a type derived from it, such as Event Hubs' <c>EventData</c> or Service Bus'
/// <c>ServiceBusMessage</c>. Its body is the Avro encoding of the value, and its content type is
/// <c>avro/binary+&lt;schema ID&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The values are types <see cref="AvroTypes"/> knows (generated from <c>.avsc</c> files, or marked
/// <c>[AvroSerializable]</c>), and generic records (<see cref="GenericRecord"/>, or an <see cref="AvroValue"/> holding
/// one).
/// </para>
/// <para>
/// A schema is registered (or looked up) once, under its full name in the serializer's group. A writer schema is
/// fetched by its ID once. The message is read in the writer's schema and resolved to the type's schema, so data of
/// older and newer versions reads. A generic record is read in the writer's schema.
/// </para>
/// </remarks>
public sealed class AvroSharpSchemaRegistrySerializer
{
    private const string AvroMimeType = "avro/binary";

    private readonly SchemaRegistryClient _client;
    private readonly string? _groupName;
    private readonly bool _autoRegisterSchemas;

    // The ID of each schema written, by its JSON, and the schema of each ID seen.
    private readonly ConcurrentDictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AvroSchema> _schemas = new(StringComparer.Ordinal);

    private readonly ConditionalWeakTable<AvroSchema, GenericDatumWriter> _genericWriters = new();
    private readonly ConcurrentDictionary<string, GenericDatumReader> _genericReaders = new(StringComparer.Ordinal);

    /// <summary>Creates a serializer.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="groupName">The schema group that schemas are registered in, or <see langword="null"/> when the serializer only reads.</param>
    /// <param name="options">The settings, or <see langword="null"/> for the defaults.</param>
    public AvroSharpSchemaRegistrySerializer(SchemaRegistryClient client, string? groupName = null, AvroSharpSchemaRegistrySerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _groupName = groupName;
        _autoRegisterSchemas = options?.AutoRegisterSchemas ?? false;
    }

    /// <summary>Writes a value into a new message of type <typeparamref name="TMessage"/>.</summary>
    /// <typeparam name="TMessage">The message type, such as <see cref="MessageContent"/>, <c>EventData</c> or <c>ServiceBusMessage</c>.</typeparam>
    /// <typeparam name="TData">The value's type.</typeparam>
    /// <param name="data">The value.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="InvalidOperationException">The serializer has no group, or <typeparamref name="TData"/> is not a type it can write.</exception>
    /// <exception cref="ArgumentException">The value's schema has no name, which the registry needs.</exception>
    public TMessage Serialize<TMessage, TData>(TData data, CancellationToken cancellationToken = default)
        where TMessage : MessageContent, new()
    {
        var (schema, body) = Encode(data, typeof(TData));
        return Message(new TMessage(), body, SchemaId(schema, cancellationToken));
    }

    /// <summary>Writes a value into a new message of type <typeparamref name="TMessage"/>.</summary>
    /// <typeparam name="TMessage">The message type, such as <see cref="MessageContent"/>, <c>EventData</c> or <c>ServiceBusMessage</c>.</typeparam>
    /// <typeparam name="TData">The value's type.</typeparam>
    /// <param name="data">The value.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="InvalidOperationException">The serializer has no group, or <typeparamref name="TData"/> is not a type it can write.</exception>
    /// <exception cref="ArgumentException">The value's schema has no name, which the registry needs.</exception>
    public async ValueTask<TMessage> SerializeAsync<TMessage, TData>(TData data, CancellationToken cancellationToken = default)
        where TMessage : MessageContent, new()
    {
        var (schema, body) = Encode(data, typeof(TData));
        return Message(new TMessage(), body, await SchemaIdAsync(schema, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Writes a value into a new message.</summary>
    /// <param name="data">The value.</param>
    /// <param name="dataType">The value's type, or <see langword="null"/> for its run-time type.</param>
    /// <param name="messageType">The message type: <see cref="MessageContent"/> (the default) or a type derived from it, with a public parameterless constructor.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="InvalidOperationException">The serializer has no group, or the value is not of a type it can write.</exception>
    /// <exception cref="ArgumentException">The value's schema has no name, or <paramref name="messageType"/> is not a <see cref="MessageContent"/>.</exception>
    public MessageContent Serialize(
        object data,
        Type? dataType = null,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type? messageType = null,
        CancellationToken cancellationToken = default)
    {
        var message = NewMessage(messageType);
        var (schema, body) = Encode(data, dataType);
        return Message(message, body, SchemaId(schema, cancellationToken));
    }

    /// <summary>Writes a value into a new message.</summary>
    /// <param name="data">The value.</param>
    /// <param name="dataType">The value's type, or <see langword="null"/> for its run-time type.</param>
    /// <param name="messageType">The message type: <see cref="MessageContent"/> (the default) or a type derived from it, with a public parameterless constructor.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="InvalidOperationException">The serializer has no group, or the value is not of a type it can write.</exception>
    /// <exception cref="ArgumentException">The value's schema has no name, or <paramref name="messageType"/> is not a <see cref="MessageContent"/>.</exception>
    public async ValueTask<MessageContent> SerializeAsync(
        object data,
        Type? dataType = null,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type? messageType = null,
        CancellationToken cancellationToken = default)
    {
        var message = NewMessage(messageType);
        var (schema, body) = Encode(data, dataType);
        return Message(message, body, await SchemaIdAsync(schema, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads a message's value as <typeparamref name="TData"/>.</summary>
    /// <typeparam name="TData">The type to read: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
    /// <param name="content">The message.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="FormatException">The content type is not <c>avro/binary+&lt;schema ID&gt;</c>.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TData"/> is not a type the serializer can read.</exception>
    public TData Deserialize<TData>(MessageContent content, CancellationToken cancellationToken = default)
    {
        var id = SchemaIdOf(content);
        return (TData)Decode(content, typeof(TData), WriterSchema(id, cancellationToken), id)!;
    }

    /// <summary>Reads a message's value as <typeparamref name="TData"/>.</summary>
    /// <typeparam name="TData">The type to read: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
    /// <param name="content">The message.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="FormatException">The content type is not <c>avro/binary+&lt;schema ID&gt;</c>.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TData"/> is not a type the serializer can read.</exception>
    public async ValueTask<TData> DeserializeAsync<TData>(MessageContent content, CancellationToken cancellationToken = default)
    {
        var id = SchemaIdOf(content);
        return (TData)Decode(content, typeof(TData), await WriterSchemaAsync(id, cancellationToken).ConfigureAwait(false), id)!;
    }

    /// <summary>Reads a message's value as <paramref name="dataType"/>.</summary>
    /// <param name="content">The message.</param>
    /// <param name="dataType">The type to read: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="FormatException">The content type is not <c>avro/binary+&lt;schema ID&gt;</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="dataType"/> is not a type the serializer can read.</exception>
    public object? Deserialize(MessageContent content, Type dataType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var id = SchemaIdOf(content);
        return Decode(content, dataType, WriterSchema(id, cancellationToken), id);
    }

    /// <summary>Reads a message's value as <paramref name="dataType"/>.</summary>
    /// <param name="content">The message.</param>
    /// <param name="dataType">The type to read: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="FormatException">The content type is not <c>avro/binary+&lt;schema ID&gt;</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="dataType"/> is not a type the serializer can read.</exception>
    public async ValueTask<object?> DeserializeAsync(MessageContent content, Type dataType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var id = SchemaIdOf(content);
        return Decode(content, dataType, await WriterSchemaAsync(id, cancellationToken).ConfigureAwait(false), id);
    }

    private static MessageContent NewMessage([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type? messageType) =>
        messageType is null
            ? new MessageContent()
            : typeof(MessageContent).IsAssignableFrom(messageType)
                ? (MessageContent)Activator.CreateInstance(messageType)!
                : throw new ArgumentException($"{messageType} is not a MessageContent.", nameof(messageType));

    private static TMessage Message<TMessage>(TMessage message, byte[] body, string schemaId)
        where TMessage : MessageContent
    {
        message.Data = BinaryData.FromBytes(body);
        message.ContentType = AvroMimeType + "+" + schemaId;
        return message;
    }

    // The schema ID from the content type: avro/binary+<ID>.
    private static string SchemaIdOf(MessageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var contentType = content.ContentType?.ToString();
        var plus = contentType?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (plus < 0
            || !string.Equals(contentType![..plus], AvroMimeType, StringComparison.Ordinal)
            || plus == contentType.Length - 1
            || contentType.IndexOf('+', plus + 1) >= 0)
        {
            throw new FormatException($"The content type '{contentType}' is not avro/binary+<schema ID>, where the schema ID is the Schema Registry's.");
        }

        return contentType[(plus + 1)..];
    }

    private (AvroSchema Schema, byte[] Body) Encode(object? data, Type? dataType)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_groupName is null)
        {
            throw new InvalidOperationException("Writing needs a schema group: pass one to the serializer's constructor. Only reading works without it.");
        }

        var output = new ArrayBufferWriter<byte>(256);
        var writer = new AvroWriter(output);
        AvroSchema schema;
        if (data is GenericRecord or AvroValue)
        {
            var value = data is GenericRecord record ? record : (AvroValue)data;
            schema = (value.IsNull ? null : value.Kind == AvroValueKind.Record ? value.AsRecord().Schema : null)
                ?? throw new ArgumentException("A generic value must be a record: the registry needs its schema's name.", nameof(data));
            _genericWriters.GetValue(schema, s => GenericDatumWriter.Create(s)).Write(ref writer, value);
        }
        else
        {
            var info = TypeInfo(dataType is null || dataType == typeof(object) ? data.GetType() : dataType);
            schema = info.Schema;
            info.WriteObject(ref writer, data);
        }

        writer.Flush();

        // The registry names each schema, by its full name.
        if (schema is not NamedSchema)
        {
            throw new ArgumentException($"Azure Schema Registry names each schema, and {schema.Type} schemas have no name: write a record.", nameof(data));
        }

        return (schema, output.WrittenSpan.ToArray());
    }

    private object? Decode(MessageContent content, Type dataType, AvroSchema writerSchema, string schemaId)
    {
        var data = content.Data ?? throw new ArgumentException("The message has no data.", nameof(content));
        var reader = new AvroReader(data.ToMemory().Span);
        if (dataType == typeof(GenericRecord) || dataType == typeof(AvroValue))
        {
            var value = _genericReaders.GetOrAdd(schemaId, _ => GenericDatumReader.Create(writerSchema)).Read(ref reader);
            return dataType == typeof(AvroValue) ? value : (object)value.AsRecord();
        }

        return TypeInfo(dataType).ReadObject(ref reader, writerSchema);
    }

    private static AvroTypeInfo TypeInfo(Type type) =>
        AvroTypes.TryGet(type, out var info)
            ? info!
            : throw new InvalidOperationException(
                $"{type} is not a type AvroSharp knows: generate it from an .avsc file or mark it [AvroSerializable] (with " +
                "AvroSharp.Generators), or use GenericRecord. On .NET Standard, register it first with AvroTypes.Register(" + type.Name + ".AvroTypeInfo).");

    private string SchemaId(AvroSchema schema, CancellationToken cancellationToken)
    {
        var json = schema.ToJson();
        if (_ids.TryGetValue(json, out var id))
        {
            return id;
        }

        var name = ((NamedSchema)schema).FullName;
        SchemaProperties properties = _autoRegisterSchemas
            ? _client.RegisterSchema(_groupName, name, json, SchemaFormat.Avro, cancellationToken)
            : _client.GetSchemaProperties(_groupName, name, json, SchemaFormat.Avro, cancellationToken);
        return Remember(json, schema, properties.Id);
    }

    private async ValueTask<string> SchemaIdAsync(AvroSchema schema, CancellationToken cancellationToken)
    {
        var json = schema.ToJson();
        if (_ids.TryGetValue(json, out var id))
        {
            return id;
        }

        var name = ((NamedSchema)schema).FullName;
        SchemaProperties properties = _autoRegisterSchemas
            ? await _client.RegisterSchemaAsync(_groupName, name, json, SchemaFormat.Avro, cancellationToken).ConfigureAwait(false)
            : await _client.GetSchemaPropertiesAsync(_groupName, name, json, SchemaFormat.Avro, cancellationToken).ConfigureAwait(false);
        return Remember(json, schema, properties.Id);
    }

    private string Remember(string json, AvroSchema schema, string id)
    {
        _schemas.TryAdd(id, schema);
        return _ids.GetOrAdd(json, id);
    }

    private AvroSchema WriterSchema(string id, CancellationToken cancellationToken) =>
        _schemas.TryGetValue(id, out var schema)
            ? schema
            : _schemas.GetOrAdd(id, AvroSchema.Parse(_client.GetSchema(id, cancellationToken).Value.Definition));

    private async ValueTask<AvroSchema> WriterSchemaAsync(string id, CancellationToken cancellationToken) =>
        _schemas.TryGetValue(id, out var schema)
            ? schema
            : _schemas.GetOrAdd(id, AvroSchema.Parse((await _client.GetSchemaAsync(id, cancellationToken).ConfigureAwait(false)).Value.Definition));
}
