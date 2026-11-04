using System;
using System.Diagnostics.CodeAnalysis;
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
    private readonly bool _strictKeys;

    internal AvroSharpGenericRecordSerializer(ISchemaRegistryClient client, AvroSharpSerializerConfig? config, RuleRegistry? ruleRegistry)
    {
        ArgumentNullException.ThrowIfNull(client);

        // The settings are checked now, not with the first record: with a schema that passes TombstoneBehavior.Strict, as
        // every record schema does.
        _ = AvroSharpGeneric.CreateSerializer(client, AvroSchema.Boolean, config, ruleRegistry);
        _strictKeys = config?.TombstoneBehavior == TombstoneBehavior.Strict;
        _create = schema => AvroSharpGeneric.CreateSerializer(client, schema, config, ruleRegistry);
    }

    /// <summary>Writes a record with its schema. A null value is a tombstone: no message body.</summary>
    /// <param name="data">The record.</param>
    /// <param name="context">The topic, the component (key or value) and the headers.</param>
    /// <returns>The message body, or <see langword="null"/> for a tombstone.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a record.</exception>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every exception goes into the returned task, as an async method's does.")]
    public Task<byte[]?> SerializeAsync(AvroValue data, SerializationContext context)
    {
        try
        {
            return data.IsNull ? Task.FromResult(Tombstone(context)) : For(data).SerializeAsync(data, context);
        }
        catch (Exception e)
        {
            return Task.FromException<byte[]?>(e);
        }
    }

    /// <summary>Writes a record with its schema, without a task once its schema ID is known. A null value is a tombstone: no message body.</summary>
    /// <param name="data">The record.</param>
    /// <param name="context">The topic, the component (key or value) and the headers.</param>
    /// <returns>The message body, or <see langword="null"/> for a tombstone.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a record.</exception>
    public byte[]? Serialize(AvroValue data, SerializationContext context) =>
        data.IsNull ? Tombstone(context) : For(data).Serialize(data, context);

    private byte[]? Tombstone(SerializationContext context) =>
        _strictKeys && context.Component == MessageComponentType.Key ? throw Tombstones.NullKey("AvroSharpGenericRecordSerializer") : null;

    private AvroSharpSerializer<AvroValue> For(AvroValue data) =>
        data.Kind == AvroValueKind.Record
            ? _serializers.GetValue(data.AsRecord().Schema, _create)
            : throw new ArgumentException($"The value is a {data.Kind}, not a record: only a record carries its schema. Write other values with a serializer of their schema (AvroSharpGeneric.CreateSerializer with a schema).", nameof(data));
}
