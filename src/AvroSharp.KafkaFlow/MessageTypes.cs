using System;
using System.Collections.Concurrent;
using AvroSharp.Confluent;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// AvroSharp.Confluent's serializers and deserializers for message types known only at run time, as KafkaFlow passes
/// them: one per type, through <see cref="AvroTypes"/>, without reflection.
/// </summary>
internal static class MessageTypes
{
    public static AvroTypeInfo Info(Type type) =>
        AvroTypes.TryGet(type, out var info)
            ? info!
            : throw new InvalidOperationException(
                $"{type} is not a type AvroSharp knows: generate it from an .avsc file or mark it [AvroSerializable] (with " +
                "AvroSharp.Generators). On .NET Standard, register it first with AvroTypes.Register(" + type.Name + ".AvroTypeInfo).");

    // The type as AvroTypeInfo<object>: the boxed read and write functions of its non-generic info.
    public static AvroTypeInfo<object> AsObject(AvroTypeInfo info) =>
        new(
            () => info.Schema,
            (ref writer, value) => info.WriteObject(ref writer, value),
            (ref reader) => info.ReadObject(ref reader)!,
            writerSchema => (ref reader) => info.ReadObject(ref reader, writerSchema)!);
}

/// <summary>A serializer of each type, made the first time the type is written.</summary>
internal sealed class SerializersByType(ISchemaRegistryClient client, AvroSharpSerializerConfig? config)
{
    private readonly ConcurrentDictionary<Type, AvroSharpSerializer<object>> _serializers = new();

    public AvroSharpSerializer<object> For(Type type) =>
        _serializers.GetOrAdd(type, t => new AvroSharpSerializer<object>(client, MessageTypes.AsObject(MessageTypes.Info(t)), config));
}

/// <summary>A deserializer of each type, made the first time the type is read.</summary>
internal sealed class DeserializersByType(ISchemaRegistryClient client, AvroSharpDeserializerConfig? config)
{
    private readonly ConcurrentDictionary<Type, AvroSharpDeserializer<object>> _deserializers = new();

    public AvroSharpDeserializer<object> For(Type type) =>
        _deserializers.GetOrAdd(type, t => new AvroSharpDeserializer<object>(client, MessageTypes.AsObject(MessageTypes.Info(t)), config));
}
