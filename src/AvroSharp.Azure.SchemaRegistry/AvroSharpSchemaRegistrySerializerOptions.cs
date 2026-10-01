namespace AvroSharp.Azure.SchemaRegistry;

/// <summary>Settings of <see cref="AvroSharpSchemaRegistrySerializer"/>, as Microsoft's <c>SchemaRegistryAvroSerializerOptions</c>.</summary>
public sealed class AvroSharpSchemaRegistrySerializerOptions
{
    /// <summary>
    /// Gets or sets whether the serializer registers a schema the group doesn't have yet. When <see langword="false"/>
    /// (the default, as in Microsoft's serializer), the schema must already be registered, and the serializer looks
    /// up its ID.
    /// </summary>
    public bool AutoRegisterSchemas { get; set; }
}
