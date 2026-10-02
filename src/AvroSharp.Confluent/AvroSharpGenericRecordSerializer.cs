using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Confluent.Kafka;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Writes generic records, each with its own schema (<see cref="GenericRecord.Schema"/>), as Confluent's generic Avro
/// serializer does: one serializer for a topic that carries records of several schemas, such as a bridge or a replay
/// tool. Each schema is registered (or looked up) under its subject once, by the subject name strategy, with the
/// record's name for the Record and TopicRecord strategies. Created by <see cref="AvroSharpGeneric.CreateSerializer(ISchemaRegistryClient, AvroSharpSerializerConfig?, RuleRegistry?)"/>.
/// </summary>
/// <remarks>
/// It is both asynchronous and synchronous, as <see cref="AvroSharpSerializer{T}"/> is. Its caches are kept per schema
/// instance, so records that share a <see cref="RecordSchema"/> share them.
/// </remarks>
public sealed class AvroSharpGenericRecordSerializer : IAsyncSerializer<AvroValue>, ISerializer<AvroValue>
{
    private readonly ConditionalWeakTable<RecordSchema, AvroSharpSerializer<AvroValue>> _serializers = new();
    private readonly ConditionalWeakTable<RecordSchema, AvroSharpSerializer<AvroValue>>.CreateValueCallback _create;

    internal AvroSharpGenericRecordSerializer(ISchemaRegistryClient client, AvroSharpSerializerConfig? config, RuleRegistry? ruleRegistry)
    {
        ArgumentNullException.ThrowIfNull(client);

        // The settings are checked now, not with the first record.
        _ = AvroSharpGeneric.CreateSerializer(client, AvroSchema.Null, config, ruleRegistry);
        _create = schema => AvroSharpGeneric.CreateSerializer(client, schema, config, ruleRegistry);
    }

    /// <summary>Writes a record with its schema. A null value is a tombstone: no message body.</summary>
    /// <param name="data">The record.</param>
    /// <param name="context">The topic, the component (key or value) and the headers.</param>
    /// <returns>The message body, or <see langword="null"/> for a tombstone.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a record.</exception>
    public Task<byte[]?> SerializeAsync(AvroValue data, SerializationContext context) =>
        data.IsNull ? Task.FromResult<byte[]?>(null) : For(data).SerializeAsync(data, context);

    /// <summary>Writes a record with its schema, without a task once its schema ID is known. A null value is a tombstone: no message body.</summary>
    /// <param name="data">The record.</param>
    /// <param name="context">The topic, the component (key or value) and the headers.</param>
    /// <returns>The message body, or <see langword="null"/> for a tombstone.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a record.</exception>
    public byte[]? Serialize(AvroValue data, SerializationContext context) =>
        data.IsNull ? null : For(data).Serialize(data, context);

    private AvroSharpSerializer<AvroValue> For(AvroValue data) =>
        data.Kind == AvroValueKind.Record
            ? _serializers.GetValue(data.AsRecord().Schema, _create)
            : throw new ArgumentException($"The value is a {data.Kind}, not a record: only a record carries its schema. Write other values with a serializer of their schema (AvroSharpGeneric.CreateSerializer with a schema).", nameof(data));
}
