using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Sets AvroSharp serializers and deserializers on Confluent.Kafka's producer and consumer builders, as their
/// synchronous interfaces, so both <c>Produce</c> and <c>ProduceAsync</c> work. They wait for the registry the first
/// time each schema is written or read.
/// </summary>
public static class AvroSharpSerdeExtensions
{
    /// <summary>Writes keys with an <see cref="AvroSharpSerializer{T}"/> of <typeparamref name="TKey"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpKeySerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetKeySerializer((ISerializer<TKey>)new AvroSharpSerializer<TKey>(client, config, ruleRegistry));
    }

    /// <summary>Writes values with an <see cref="AvroSharpSerializer{T}"/> of <typeparamref name="TValue"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ProducerBuilder<TKey, TValue> SetAvroSharpValueSerializer<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer((ISerializer<TValue>)new AvroSharpSerializer<TValue>(client, config, ruleRegistry));
    }

    /// <summary>
    /// Writes keys with a version of a subject, pinned: an <see cref="AvroSharpSerializer{T}"/> with that version's
    /// schema ID (<c>use.schema.id</c>), under that subject for every topic. A key type whose schema differs from the
    /// version's is written in it, when the version can read the type's schema (<c>id.compatibility.strict</c>).
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry, which is asked for the version once.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="version">The version, from 1.</param>
    /// <param name="config">Other settings, or <see langword="null"/> for the defaults; <c>use.schema.id</c> and <c>auto.register.schemas</c> are set.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static async Task<ProducerBuilder<TKey, TValue>> SetAvroSharpKeySerializerAsync<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, string subject, int version, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetKeySerializer((ISerializer<TKey>)await PinnedAsync<TKey>(client, subject, version, config, ruleRegistry).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes values with a version of a subject, pinned: an <see cref="AvroSharpSerializer{T}"/> with that version's
    /// schema ID (<c>use.schema.id</c>), under that subject for every topic. A value type whose schema differs from the
    /// version's is written in it, when the version can read the type's schema (<c>id.compatibility.strict</c>).
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry, which is asked for the version once.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="version">The version, from 1.</param>
    /// <param name="config">Other settings, or <see langword="null"/> for the defaults; <c>use.schema.id</c> and <c>auto.register.schemas</c> are set.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static async Task<ProducerBuilder<TKey, TValue>> SetAvroSharpValueSerializerAsync<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, string subject, int version, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer((ISerializer<TValue>)await PinnedAsync<TValue>(client, subject, version, config, ruleRegistry).ConfigureAwait(false));
    }

    // A serializer of the version's schema ID, under the subject.
    private static async Task<AvroSharpSerializer<T>> PinnedAsync<T>(ISchemaRegistryClient client, string subject, int version, AvroSharpSerializerConfig? config, RuleRegistry? ruleRegistry)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(subject);
        var registered = await client.GetRegisteredSchemaAsync(subject, version, ignoreDeletedSchemas: true).ConfigureAwait(false);
        var pinned = new AvroSharpSerializerConfig(config ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            UseSchemaId = registered.Id,
            AutoRegisterSchemas = false,
        };
        return new AvroSharpSerializer<T>(client, pinned, ruleRegistry) { CustomSubjectNameStrategy = (_, _) => Task.FromResult(subject) };
    }

    /// <summary>
    /// Writes generic values (<see cref="AvroValue"/>): of <paramref name="schema"/>, or, without one, each record with
    /// its own schema (<see cref="AvroSharpGeneric"/>).
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="schema">The schema the values are written with, or <see langword="null"/> to write each record with its own.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ProducerBuilder<TKey, AvroValue> SetAvroSharpGenericValueSerializer<TKey>(this ProducerBuilder<TKey, AvroValue> builder, ISchemaRegistryClient client, AvroSchema? schema = null, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueSerializer(schema is null
            ? AvroSharpGeneric.CreateSerializer(client, config, ruleRegistry)
            : (ISerializer<AvroValue>)AvroSharpGeneric.CreateSerializer(client, schema, config, ruleRegistry));
    }

    /// <summary>
    /// Reads generic values (<see cref="AvroValue"/>): each message in its writer's schema, or resolved to
    /// <paramref name="readerSchema"/> (<see cref="AvroSharpGeneric.CreateDeserializer"/>).
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="readerSchema">The schema to read every message as, or <see langword="null"/> to read each as written.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ConsumerBuilder<TKey, AvroValue> SetAvroSharpGenericValueDeserializer<TKey>(this ConsumerBuilder<TKey, AvroValue> builder, ISchemaRegistryClient client, AvroSchema? readerSchema = null, AvroSharpDeserializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueDeserializer(AvroSharpGeneric.CreateDeserializer(client, readerSchema, config, ruleRegistry));
    }

    /// <summary>Reads keys with an <see cref="AvroSharpDeserializer{T}"/> of <typeparamref name="TKey"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ConsumerBuilder<TKey, TValue> SetAvroSharpKeyDeserializer<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpDeserializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetKeyDeserializer(new AvroSharpDeserializer<TKey>(client, config, ruleRegistry));
    }

    /// <summary>Reads values with an <see cref="AvroSharpDeserializer{T}"/> of <typeparamref name="TValue"/>, which <see cref="AvroTypes"/> knows.</summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="client">The schema registry.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static ConsumerBuilder<TKey, TValue> SetAvroSharpValueDeserializer<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder, ISchemaRegistryClient client, AvroSharpDeserializerConfig? config = null, RuleRegistry? ruleRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SetValueDeserializer(new AvroSharpDeserializer<TValue>(client, config, ruleRegistry));
    }
}
