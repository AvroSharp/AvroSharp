using System;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Serializers and deserializers of generic values (<see cref="AvroValue"/>, such as a <see cref="GenericRecord"/>),
/// for code that has schemas rather than generated types.
/// </summary>
public static class AvroSharpGeneric
{
    /// <summary>Creates a serializer of values of <paramref name="schema"/>, which it registers under the subject.</summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="schema">The schema the values are written with.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static AvroSharpSerializer<AvroValue> CreateSerializer(ISchemaRegistryClient client, AvroSchema schema, AvroSharpSerializerConfig? config = null, RuleRegistry? ruleRegistry = null) =>
        new(client, TypeInfo(schema, readerSchema: schema), config, ruleRegistry);

    /// <summary>
    /// Creates a deserializer of generic values: each message as its writer's schema, or resolved to
    /// <paramref name="readerSchema"/> when one is given. Without one, and with <c>use.latest.version</c> or
    /// <c>use.latest.with.metadata</c>, each message is resolved to the subject's latest schema, as Confluent's generic
    /// deserializer reads.
    /// </summary>
    /// <param name="client">The schema registry.</param>
    /// <param name="readerSchema">The schema to read every message as, or <see langword="null"/> to read each as written (or as the latest schema, with the settings above).</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="ruleRegistry">The data contract rules, or <see langword="null"/> for <see cref="RuleRegistry.GlobalInstance"/>.</param>
    public static AvroSharpDeserializer<AvroValue> CreateDeserializer(ISchemaRegistryClient client, AvroSchema? readerSchema = null, AvroSharpDeserializerConfig? config = null, RuleRegistry? ruleRegistry = null) =>
        new(client, TypeInfo(readerSchema ?? AvroSchema.Parse("\"null\""), readerSchema), config, ruleRegistry)
        {
            // Without a reader schema, values are read as the latest schema when the settings name one, as Confluent's
            // generic deserializer reads them, and as each writer's otherwise.
            ReadAsLatest = readerSchema is null ? ReadAs : null,
        };

    private static AvroReadFunc<AvroValue> ReadAs(AvroSchema writerSchema, AvroSchema latestSchema)
    {
        var resolving = GenericDatumReader.Create(writerSchema, latestSchema);
        return (ref r) => resolving.Read(ref r);
    }

    private static AvroTypeInfo<AvroValue> TypeInfo(AvroSchema schema, AvroSchema? readerSchema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var writer = GenericDatumWriter.Create(schema);
        var reader = GenericDatumReader.Create(schema);
        return new AvroTypeInfo<AvroValue>(
            () => schema,
            (ref w, value) => writer.Write(ref w, value),
            (ref r) => reader.Read(ref r),
            writerSchema =>
            {
                var resolving = readerSchema is null ? GenericDatumReader.Create(writerSchema) : GenericDatumReader.Create(writerSchema, readerSchema);
                return (ref r) => resolving.Read(ref r);
            });
    }
}
