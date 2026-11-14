using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AvroSharp.CodeGen;

/// <summary>
/// Files that refer to each other's types (#16): neither parses alone, because a reference must follow its
/// definition. A file is parsed with the definitions it needs from the other files inlined at their first use, which is
/// how the types would be written in one file; each inlined definition gets its full name and an explicit namespace,
/// so names in and around it resolve as they did in its own file.
/// </summary>
internal static class SchemaFileCycles
{
    private static readonly HashSet<string> s_primitives = new(StringComparer.Ordinal)
    {
        "null", "boolean", "int", "long", "float", "double", "bytes", "string",
    };

    private static readonly HashSet<string> s_named = new(StringComparer.Ordinal) { "record", "error", "enum", "fixed" };

    /// <summary>The named types each file defines, by full name: the file and the definition's JSON.</summary>
    public static Dictionary<string, (string Path, JsonObject Definition)> Definitions(IEnumerable<(string Path, string Json)> files)
    {
        var definitions = new Dictionary<string, (string Path, JsonObject Definition)>(StringComparer.Ordinal);
        foreach (var (path, json) in files)
        {
            if (TryParse(json) is { } root)
            {
                Collect(root, enclosingNamespace: null, path, definitions);
            }
        }

        return definitions;
    }

    /// <summary>
    /// The file's JSON with the definitions it needs from other files inlined, or <see langword="null"/> when it needs
    /// none. <paramref name="inlined"/> receives the full names inlined.
    /// </summary>
    /// <param name="json">The file's JSON.</param>
    /// <param name="path">The file's path: its own definitions are never inlined.</param>
    /// <param name="definitions">The definitions of the files not parsed yet.</param>
    /// <param name="defined">Whether a type is defined already, by an earlier file.</param>
    /// <param name="inlined">The full names inlined.</param>
    public static string? Inline(string json, string path, Dictionary<string, (string Path, JsonObject Definition)> definitions, Func<string, bool> defined, List<string> inlined)
    {
        if (TryParse(json) is not { } root)
        {
            return null;
        }

        // The names this document defines: the file's own types, and later every type in an inlined definition.
        var own = new Dictionary<string, (string Path, JsonObject Definition)>(StringComparer.Ordinal);
        Collect(root, enclosingNamespace: null, path, own);
        var context = new Context(path, definitions, defined, inlined, new HashSet<string>(own.Keys, StringComparer.Ordinal));
        var result = Visit(root, enclosingNamespace: null, typePosition: true, context);
        return inlined.Count == 0 ? null : result!.ToJsonString();
    }

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Replaces references to types defined in other files, at their first use, with those definitions.
    private static JsonNode? Visit(JsonNode? node, string? enclosingNamespace, bool typePosition, Context context)
    {
        switch (node)
        {
            case JsonValue value when typePosition && value.TryGetValue<string>(out var name):
                return Resolve(name, enclosingNamespace, context) is { } fullName ? InlineDefinition(fullName, context) : node;
            case JsonArray union when typePosition:
                for (var i = 0; i < union.Count; i++)
                {
                    var replaced = Visit(union[i], enclosingNamespace, typePosition: true, context);
                    if (!ReferenceEquals(replaced, union[i]))
                    {
                        union[i] = replaced;
                    }
                }

                return node;
            // A reference written as an object, {"type": "ns.Other"}: the whole object is the reference.
            case JsonObject reference when typePosition && reference["type"] is JsonValue type && type.TryGetValue<string>(out var typeName)
                && Resolve(typeName, enclosingNamespace, context) is { } referenced:
                return InlineDefinition(referenced, context);
            case JsonObject schema:
                VisitObject(schema, enclosingNamespace, context);
                return node;
            default:
                return node;
        }
    }

    private static void VisitObject(JsonObject schema, string? enclosingNamespace, Context context)
    {
        // A named type's own namespace (null for the null namespace) holds for the names inside it.
        var ns = FullNameOf(schema, enclosingNamespace) is { } fullName ? NamespaceOf(fullName) : enclosingNamespace;
        foreach (var key in new[] { "type", "items", "values" })
        {
            ReplaceChild(schema, key, ns, context);
        }

        if (schema["fields"] is JsonArray fields)
        {
            foreach (var field in fields.OfType<JsonObject>())
            {
                ReplaceChild(field, "type", ns, context);
            }
        }
    }

    // Visits the schema at a property, a type position, and puts back what replaces it.
    private static void ReplaceChild(JsonObject parent, string key, string? ns, Context context)
    {
        if (parent[key] is not { } child)
        {
            return;
        }

        var replaced = Visit(child, ns, typePosition: true, context);
        if (!ReferenceEquals(replaced, child))
        {
            parent[key] = replaced;
        }
    }

    // The full name a reference resolves to among the other files' definitions, when it should be inlined here.
    private static string? Resolve(string name, string? enclosingNamespace, Context context)
    {
        if (s_primitives.Contains(name) || s_named.Contains(name) || name is "array" or "map")
        {
            return null;
        }

        // An unqualified name is looked up in the enclosing namespace, then in the null namespace, as in Java.
        var candidates = name.Contains('.', StringComparison.Ordinal) || string.IsNullOrEmpty(enclosingNamespace)
            ? new[] { name }
            : new[] { enclosingNamespace + "." + name, name };
        // The first candidate that names a type wins, as in the parser: one defined already (by an earlier file, this
        // file, or an inlined definition) is left as a reference; one only another pending file defines is inlined.
        foreach (var fullName in candidates)
        {
            if (context.Defined(fullName) || context.InDocument.Contains(fullName))
            {
                return null;
            }

            if (context.Definitions.TryGetValue(fullName, out var definition) && !string.Equals(definition.Path, context.Path, StringComparison.Ordinal))
            {
                return fullName;
            }
        }

        return null;
    }

    private static JsonObject InlineDefinition(string fullName, Context context)
    {
        context.Inlined.Add(fullName);
        var copy = (JsonObject)context.Definitions[fullName].Definition.DeepClone();
        var dot = fullName.LastIndexOf('.');
        copy["name"] = fullName;
        copy["namespace"] = dot < 0 ? string.Empty : fullName[..dot];

        // Every type the copy defines, nested ones too, is defined from here on in this document.
        var nested = new Dictionary<string, (string Path, JsonObject Definition)>(StringComparer.Ordinal);
        Collect(copy, enclosingNamespace: null, context.Path, nested);
        context.InDocument.UnionWith(nested.Keys);
        VisitObject(copy, enclosingNamespace: null, context);
        return copy;
    }

    private static void Collect(JsonNode? node, string? enclosingNamespace, string path, Dictionary<string, (string Path, JsonObject Definition)> definitions)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, enclosingNamespace, path, definitions);
                }

                break;
            case JsonObject schema:
                var ns = enclosingNamespace;
                if (FullNameOf(schema, enclosingNamespace) is { } fullName)
                {
                    definitions.TryAdd(fullName, (path, schema));
                    ns = NamespaceOf(fullName);
                }

                foreach (var property in schema.Where(p => p.Key is not "default"))
                {
                    Collect(property.Value, ns, path, definitions);
                }

                break;
        }
    }

    private static string? NamespaceOf(string fullName) => fullName.LastIndexOf('.') is var dot and >= 0 ? fullName[..dot] : null;

    private static string? FullNameOf(JsonObject schema, string? enclosingNamespace)
    {
        if (schema["type"] is not JsonValue type || !type.TryGetValue<string>(out var kind) || !s_named.Contains(kind)
            || schema["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
        {
            return null;
        }

        if (name.Contains('.', StringComparison.Ordinal))
        {
            return name;
        }

        var ns = schema["namespace"] is JsonValue nsValue && nsValue.TryGetValue<string>(out var explicitNamespace) ? explicitNamespace : enclosingNamespace;
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private sealed record Context(string Path, Dictionary<string, (string Path, JsonObject Definition)> Definitions, Func<string, bool> Defined, List<string> Inlined, HashSet<string> InDocument);
}
