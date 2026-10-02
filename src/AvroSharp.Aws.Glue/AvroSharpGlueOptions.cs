using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Amazon.Glue;
using AvroSharp.Schemas;

namespace AvroSharp.Aws.Glue;

/// <summary>Whether the serializer compresses the Avro data, as AWS's serializer's <c>compression</c> setting.</summary>
public enum AvroSharpGlueCompression
{
    /// <summary>No compression (AWS's default): the compression byte <c>0x00</c>.</summary>
    None,

    /// <summary>zlib: the compression byte <c>0x05</c>, and the Avro data zlib-compressed.</summary>
    Zlib,
}

/// <summary>
/// Settings of <see cref="AvroSharpGlueSerializer"/> and its Kafka adapters, with the defaults of AWS's Glue Schema
/// Registry serializer. Each property names AWS's configuration key it stands for.
/// </summary>
public sealed class AvroSharpGlueOptions
{
    /// <summary>Gets or sets the registry the schemas are in (<c>registry.name</c>). The default is <c>default-registry</c>, as in AWS's serializer.</summary>
    public string RegistryName { get; set; } = "default-registry";

    /// <summary>
    /// Gets or sets the name of the schema every value is registered under (<c>schemaName</c>). When it is
    /// <see langword="null"/>, the default, <see cref="SchemaNameStrategy"/> names it.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// Gets or sets how a schema is named when <see cref="SchemaName"/> isn't set (<c>schemaNameGenerationClass</c>):
    /// from the transport name (the Kafka topic, or the Kinesis stream) and the value's schema. When it is
    /// <see langword="null"/>, the default, the schema is named after the transport, as AWS's default strategy does.
    /// </summary>
    public Func<string, AvroSchema, string>? SchemaNameStrategy { get; set; }

    /// <summary>
    /// Gets or sets whether the serializer registers a schema the registry doesn't have
    /// (<c>schemaAutoRegistrationEnabled</c>): a new version of an existing schema, or a new schema. The default is
    /// <see langword="false"/>, as in AWS's serializer: the schema version must already exist.
    /// </summary>
    public bool AutoRegisterSchemas { get; set; }

    /// <summary>Gets or sets whether the Avro data is compressed (<c>compression</c>). The default is <see cref="AvroSharpGlueCompression.None"/>.</summary>
    public AvroSharpGlueCompression Compression { get; set; }

    /// <summary>
    /// Gets or sets the compatibility mode of a schema the serializer creates (<c>compatibility</c>). The default is
    /// <see cref="Compatibility.BACKWARD"/>, as in AWS's serializer.
    /// </summary>
    public Compatibility Compatibility { get; set; } = Compatibility.BACKWARD;

    /// <summary>Gets or sets the description of a schema the serializer creates (<c>description</c>).</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the tags of a schema the serializer creates (<c>tags</c>).</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Settings, set as a whole like the others.")]
    public IDictionary<string, string>? Tags { get; set; }

    /// <summary>
    /// Gets or sets how long to wait between checks of a new schema version that is still pending, while the
    /// registry checks its compatibility. The default is 3 seconds, as in AWS's serializer, which checks 10 times.
    /// </summary>
    public TimeSpan PendingVersionInterval { get; set; } = TimeSpan.FromSeconds(3);
}
