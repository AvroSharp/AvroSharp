using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Settings of <see cref="AvroSharpSerializer{T}"/> and the serializers of <see cref="AvroSharpGeneric"/>, with the keys of
/// Confluent's <c>AvroSerializerConfig</c> (<c>avro.serializer.*</c>), so existing configuration carries over.
/// </summary>
public sealed class AvroSharpSerializerConfig : SerdeConfig
{
    /// <summary>Creates empty settings: every value is the serializer's default.</summary>
    public AvroSharpSerializerConfig()
    {
    }

    /// <summary>Creates settings from key-value pairs, such as a configuration section.</summary>
    /// <param name="config">The settings, by key.</param>
    public AvroSharpSerializerConfig(IDictionary<string, string> config)
        : base(config)
    {
    }

    /// <summary>Gets or sets the initial size of the buffer a message is written into (<c>avro.serializer.buffer.bytes</c>).</summary>
    public int? BufferBytes
    {
        get => GetInt(PropertyNames.BufferBytes);
        set => SetObject(PropertyNames.BufferBytes, value);
    }

    /// <summary>
    /// Gets or sets whether the serializer registers the schema when the subject lacks it
    /// (<c>avro.serializer.auto.register.schemas</c>). The default is <see langword="true"/>.
    /// </summary>
    public bool? AutoRegisterSchemas
    {
        get => GetBool(PropertyNames.AutoRegisterSchemas);
        set => SetObject(PropertyNames.AutoRegisterSchemas, value);
    }

    /// <summary>Gets or sets whether the registry normalizes schemas when they are registered or looked up (<c>avro.serializer.normalize.schemas</c>).</summary>
    public bool? NormalizeSchemas
    {
        get => GetBool(PropertyNames.NormalizeSchemas);
        set => SetObject(PropertyNames.NormalizeSchemas, value);
    }

    /// <summary>Gets or sets the ID of the registered schema to write with, instead of looking it up (<c>avro.serializer.use.schema.id</c>).</summary>
    public int? UseSchemaId
    {
        get => GetInt(PropertyNames.UseSchemaId);
        set => SetObject(PropertyNames.UseSchemaId, value);
    }

    /// <summary>Gets or sets whether the serializer writes with the subject's latest schema (<c>avro.serializer.use.latest.version</c>).</summary>
    public bool? UseLatestVersion
    {
        get => GetBool(PropertyNames.UseLatestVersion);
        set => SetObject(PropertyNames.UseLatestVersion, value);
    }

    /// <summary>Gets or sets the metadata that selects the subject's latest schema to write with (<c>avro.serializer.use.latest.with.metadata</c>).</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "The shape of Confluent's config classes, which this one mirrors.")]
    public IDictionary<string, string>? UseLatestWithMetadata
    {
        get => GetDictionaryProperty(PropertyNames.UseLatestWithMetadata);
        set => SetDictionaryProperty(PropertyNames.UseLatestWithMetadata, value);
    }

    /// <summary>
    /// Gets or sets how the subject is named (<c>avro.serializer.subject.name.strategy</c>). The default is
    /// <see cref="global::Confluent.SchemaRegistry.SubjectNameStrategy.Associated"/>, as in Confluent's serde: the topic's
    /// association in the registry, or <see cref="global::Confluent.SchemaRegistry.SubjectNameStrategy.Topic"/> when it has none.
    /// </summary>
    public SubjectNameStrategy? SubjectNameStrategy
    {
        get => (SubjectNameStrategy?)GetEnum(typeof(SubjectNameStrategy), PropertyNames.SubjectNameStrategy);
        set => SetObject(PropertyNames.SubjectNameStrategy, value);
    }

    /// <summary>Gets or sets where the schema ID goes: the message prefix (the default) or a header (<c>avro.serializer.schema.id.strategy</c>).</summary>
    public SchemaIdSerializerStrategy? SchemaIdStrategy
    {
        get => (SchemaIdSerializerStrategy?)GetEnum(typeof(SchemaIdSerializerStrategy), PropertyNames.SchemaIdStrategy);
        set => SetObject(PropertyNames.SchemaIdStrategy, value);
    }

    /// <summary>The configuration keys, as Confluent's <c>AvroSerializerConfig.PropertyNames</c>.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "The shape of Confluent's config classes, which this one mirrors.")]
    public static class PropertyNames
    {
        /// <summary>The initial buffer size.</summary>
        public const string BufferBytes = "avro.serializer.buffer.bytes";

        /// <summary>Whether to register schemas.</summary>
        public const string AutoRegisterSchemas = "avro.serializer.auto.register.schemas";

        /// <summary>Whether to normalize schemas.</summary>
        public const string NormalizeSchemas = "avro.serializer.normalize.schemas";

        /// <summary>The schema ID to write with.</summary>
        public const string UseSchemaId = "avro.serializer.use.schema.id";

        /// <summary>Whether to write with the latest schema.</summary>
        public const string UseLatestVersion = "avro.serializer.use.latest.version";

        /// <summary>The metadata selecting the latest schema.</summary>
        public const string UseLatestWithMetadata = "avro.serializer.use.latest.with.metadata";

        /// <summary>The subject name strategy.</summary>
        public const string SubjectNameStrategy = "avro.serializer.subject.name.strategy";

        /// <summary>The schema ID strategy.</summary>
        public const string SchemaIdStrategy = "avro.serializer.schema.id.strategy";
    }
}
