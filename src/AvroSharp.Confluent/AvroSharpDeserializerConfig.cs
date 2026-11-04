using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Settings of <see cref="AvroSharpDeserializer{T}"/> and the deserializers of <see cref="AvroSharpGeneric"/>, with the keys of
/// Confluent's <c>AvroDeserializerConfig</c> (<c>avro.deserializer.*</c>), so existing configuration carries over.
/// </summary>
public sealed class AvroSharpDeserializerConfig : SerdeConfig
{
    /// <summary>Creates empty settings: every value is the deserializer's default.</summary>
    public AvroSharpDeserializerConfig()
    {
    }

    /// <summary>
    /// Creates settings from key-value pairs, copied, as Confluent's config classes do: a dictionary, or a
    /// configuration section's pairs (see the guide). Pairs without a value are left out.
    /// </summary>
    /// <param name="config">The settings, by key.</param>
    public AvroSharpDeserializerConfig(IEnumerable<KeyValuePair<string, string>> config)
        : base(ConfigKeys.Copy(config))
    {
    }

    /// <summary>
    /// Gets or sets whether migration and domain rules apply with the subject's latest schema
    /// (<c>avro.deserializer.use.latest.version</c>). A type's values are read as its own schema either way; generic
    /// values without a reader schema (<see cref="AvroSharpGeneric"/>) are read as the latest schema.
    /// </summary>
    public bool? UseLatestVersion
    {
        get => GetBool(PropertyNames.UseLatestVersion);
        set => SetObject(PropertyNames.UseLatestVersion, value);
    }

    /// <summary>Gets or sets the metadata that selects the subject's latest schema (<c>avro.deserializer.use.latest.with.metadata</c>).</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "The shape of Confluent's config classes, which this one mirrors.")]
    public IDictionary<string, string>? UseLatestWithMetadata
    {
        get => GetDictionaryProperty(PropertyNames.UseLatestWithMetadata);
        set => SetDictionaryProperty(PropertyNames.UseLatestWithMetadata, value);
    }

    /// <summary>
    /// Gets or sets how the subject is named (<c>avro.deserializer.subject.name.strategy</c>). The default is
    /// <see cref="global::Confluent.SchemaRegistry.SubjectNameStrategy.Associated"/>, as in Confluent's serde: the topic's
    /// association in the registry, or <see cref="global::Confluent.SchemaRegistry.SubjectNameStrategy.Topic"/> when it has none.
    /// </summary>
    public SubjectNameStrategy? SubjectNameStrategy
    {
        get => (SubjectNameStrategy?)GetEnum(typeof(SubjectNameStrategy), PropertyNames.SubjectNameStrategy);
        set => SetObject(PropertyNames.SubjectNameStrategy, value);
    }

    /// <summary>Gets or sets where the schema ID is read from: the message prefix, a header, or both (<c>avro.deserializer.schema.id.strategy</c>).</summary>
    public SchemaIdDeserializerStrategy? SchemaIdStrategy
    {
        get => (SchemaIdDeserializerStrategy?)GetEnum(typeof(SchemaIdDeserializerStrategy), PropertyNames.SchemaIdStrategy);
        set => SetObject(PropertyNames.SchemaIdStrategy, value);
    }

    /// <summary>
    /// Gets or sets whether a message with bytes left after the value fails to read
    /// (<c>avro.deserializer.fail.on.trailing.data</c>), as Java's <c>avro.fail.on.trailing.data</c>. Such bytes are a
    /// sign of a wrong schema or a corrupt message. The default is <see langword="false"/>: they are ignored.
    /// </summary>
    public bool? FailOnTrailingData
    {
        get => GetBool(PropertyNames.FailOnTrailingData);
        set => SetObject(PropertyNames.FailOnTrailingData, value);
    }

    /// <summary>
    /// Gets or sets how tombstones are treated (<c>avro.deserializer.tombstone.behavior</c>, AvroSharp's own key). The
    /// default is <see cref="Confluent.TombstoneBehavior.None"/>, Confluent's behavior.
    /// </summary>
    public TombstoneBehavior? TombstoneBehavior
    {
        get => (TombstoneBehavior?)GetEnum(typeof(TombstoneBehavior), PropertyNames.TombstoneBehavior);
        set => SetObject(PropertyNames.TombstoneBehavior, value);
    }

    /// <summary>The configuration keys, as Confluent's <c>AvroDeserializerConfig.PropertyNames</c>.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "The shape of Confluent's config classes, which this one mirrors.")]
    public static class PropertyNames
    {
        /// <summary>Whether to apply rules with the latest schema.</summary>
        public const string UseLatestVersion = "avro.deserializer.use.latest.version";

        /// <summary>The metadata selecting the latest schema.</summary>
        public const string UseLatestWithMetadata = "avro.deserializer.use.latest.with.metadata";

        /// <summary>The subject name strategy.</summary>
        public const string SubjectNameStrategy = "avro.deserializer.subject.name.strategy";

        /// <summary>The schema ID strategy.</summary>
        public const string SchemaIdStrategy = "avro.deserializer.schema.id.strategy";

        /// <summary>Whether bytes left after the value fail the read.</summary>
        public const string FailOnTrailingData = "avro.deserializer.fail.on.trailing.data";

        /// <summary>How tombstones are treated.</summary>
        public const string TombstoneBehavior = "avro.deserializer.tombstone.behavior";
    }
}
