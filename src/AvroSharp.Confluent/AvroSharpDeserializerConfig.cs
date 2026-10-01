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

    /// <summary>Creates settings from key-value pairs, such as a configuration section.</summary>
    /// <param name="config">The settings, by key.</param>
    public AvroSharpDeserializerConfig(IDictionary<string, string> config)
        : base(config)
    {
    }

    /// <summary>
    /// Gets or sets whether migration and domain rules apply with the subject's latest schema
    /// (<c>avro.deserializer.use.latest.version</c>). Values are read as the target type's schema either way.
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
    }
}
