using System;
using System.Collections.Generic;

namespace AvroSharp.Confluent;

/// <summary>Rejects configuration keys the serializers don't know, as Confluent's do, so a misspelled key isn't ignored.</summary>
internal static class ConfigKeys
{
    public static readonly HashSet<string> Serializer = new(StringComparer.Ordinal)
    {
        AvroSharpSerializerConfig.PropertyNames.BufferBytes,
        AvroSharpSerializerConfig.PropertyNames.AutoRegisterSchemas,
        AvroSharpSerializerConfig.PropertyNames.NormalizeSchemas,
        AvroSharpSerializerConfig.PropertyNames.UseSchemaId,
        AvroSharpSerializerConfig.PropertyNames.UseLatestVersion,
        AvroSharpSerializerConfig.PropertyNames.UseLatestWithMetadata,
        AvroSharpSerializerConfig.PropertyNames.SubjectNameStrategy,
        AvroSharpSerializerConfig.PropertyNames.SchemaIdStrategy,
    };

    public static readonly HashSet<string> Deserializer = new(StringComparer.Ordinal)
    {
        AvroSharpDeserializerConfig.PropertyNames.UseLatestVersion,
        AvroSharpDeserializerConfig.PropertyNames.UseLatestWithMetadata,
        AvroSharpDeserializerConfig.PropertyNames.SubjectNameStrategy,
        AvroSharpDeserializerConfig.PropertyNames.SchemaIdStrategy,
    };

    /// <summary>
    /// Checks every key: one of <paramref name="known"/>, or a key of the rules (<c>rules.*</c>) or of the
    /// <c>Associated</c> subject name strategy (<c>subject.name.strategy.*</c>), which Confluent's code reads.
    /// </summary>
    public static void Check(IEnumerable<KeyValuePair<string, string>> config, HashSet<string> known, string serde)
    {
        foreach (var item in config)
        {
            if (!known.Contains(item.Key)
                && !item.Key.StartsWith("rules.", StringComparison.Ordinal)
                && !item.Key.StartsWith("subject.name.strategy.", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{serde}: unknown configuration key '{item.Key}'.", nameof(config));
            }
        }
    }
}
