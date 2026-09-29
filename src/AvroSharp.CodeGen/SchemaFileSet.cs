using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>
/// A set of schema files parsed together, so a file may refer to named types that another file defines, whatever the
/// order of the files. Used by the source generator and the <c>avrosharp</c> tool.
/// </summary>
/// <remarks>
/// Files are parsed in ordinal path order with one parser; a file whose references are not defined yet is retried
/// after the others, until no more files can be parsed. A type repeated identically in several files (as schema sets
/// written for Apache.Avro's one-file-at-a-time tooling often do) is accepted once.
/// </remarks>
public sealed class SchemaFileSet
{
    // The named types each file that could not be parsed declares, by full name: for errors that name a type such a
    // file defines (circular references between files, #131).
    private readonly Dictionary<string, string> _declaredInFailed;

    private SchemaFileSet(IReadOnlyList<AvroSchema> schemas, IReadOnlyDictionary<string, AvroSchema> schemasByPath, IReadOnlyDictionary<string, AvroSchemaException> errors, IReadOnlyDictionary<string, string> definedIn, Dictionary<string, string> declaredInFailed)
    {
        Schemas = schemas;
        SchemasByPath = schemasByPath;
        Errors = errors;
        DefinedIn = definedIn;
        _declaredInFailed = declaredInFailed;
    }

    /// <summary>Gets the schemas of the files that parsed, in the order they parsed.</summary>
    public IReadOnlyList<AvroSchema> Schemas { get; }

    /// <summary>Gets the schema of each file that parsed, by path.</summary>
    public IReadOnlyDictionary<string, AvroSchema> SchemasByPath { get; }

    /// <summary>Gets the error of each file that could not be parsed, by path.</summary>
    public IReadOnlyDictionary<string, AvroSchemaException> Errors { get; }

    /// <summary>Gets the path of the file that first defined each named type, by full name.</summary>
    public IReadOnlyDictionary<string, string> DefinedIn { get; }

    /// <summary>
    /// Gets the error message of a file that could not be parsed. When the file defines again a type that another file
    /// defines differently, the message names that other file; when it uses a type that another file which could not
    /// be parsed defines, it names that file, and says whether the two files need each other's types.
    /// </summary>
    /// <param name="path">The file's path, as given to <see cref="Parse"/>.</param>
    /// <exception cref="KeyNotFoundException">The file parsed, or is not in the set.</exception>
    public string GetErrorMessage(string path)
    {
        var message = Errors[path].Message;
        foreach (var pair in DefinedIn)
        {
            if (message.Contains("'" + pair.Key + "' is already defined", StringComparison.Ordinal) && !string.Equals(pair.Value, path, StringComparison.Ordinal))
            {
                return message + $" It is also defined in {pair.Value}.";
            }
        }

        if (Undefined(message) is { } needed && _declaredInFailed.TryGetValue(needed, out var other) && !string.Equals(other, path, StringComparison.Ordinal))
        {
            // The other file failed too. If it needs one of this file's types, neither can be parsed first.
            var neededBack = Errors.TryGetValue(other, out var otherError) ? Undefined(otherError.Message) : null;
            return neededBack is not null && _declaredInFailed.TryGetValue(neededBack, out var back) && string.Equals(back, path, StringComparison.Ordinal)
                ? message + $" It is defined in {other}, which itself needs '{neededBack}' from this file: circular references between files are not supported. Define the types that refer to each other in one file."
                : message + $" It is defined in {other}, which could not be parsed either.";
        }

        return message;
    }

    // The type an "is not a defined type" error names, or null.
    private static string? Undefined(string message)
    {
        const string Marker = "' is not a defined type";
        var end = message.IndexOf(Marker, StringComparison.Ordinal);
        var start = end > 0 ? message.LastIndexOf('\'', end - 1) : -1;
        return start >= 0 ? message.Substring(start + 1, end - start - 1) : null;
    }

    // The full names of the named types a schema's JSON declares, found without resolving references.
    private static List<string> Declared(string json)
    {
        var names = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Walk(document.RootElement, enclosingNamespace: null, names);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: its parse error says so, and it declares nothing another file could need.
        }

        return names;
    }

    private static void Walk(System.Text.Json.JsonElement element, string? enclosingNamespace, List<string> names)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, enclosingNamespace, names);
                }

                break;
            case System.Text.Json.JsonValueKind.Object:
                var ns = enclosingNamespace;
                if (element.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String
                    && type.GetString() is "record" or "error" or "enum" or "fixed"
                    && element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var name = nameElement.GetString()!;
                    if (!name.Contains('.', StringComparison.Ordinal) && element.TryGetProperty("namespace", out var nsElement) && nsElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        ns = nsElement.GetString();
                    }

                    var fullName = name.Contains('.', StringComparison.Ordinal) || string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    names.Add(fullName);
                    var dot = fullName.LastIndexOf('.');
                    ns = dot < 0 ? null : fullName[..dot];
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, ns, names);
                }

                break;
        }
    }

    /// <summary>Parses schema files together.</summary>
    /// <param name="files">The files: a path, which identifies the file in errors, and its JSON.</param>
    /// <param name="cancellationToken">A token to cancel the parsing.</param>
    public static SchemaFileSet Parse(IEnumerable<(string Path, string Json)> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var parser = new AvroSchemaParser(new AvroSchemaParseOptions { AllowIdenticalRedefinitions = true });
        var schemas = new List<AvroSchema>();
        var schemasByPath = new Dictionary<string, AvroSchema>(StringComparer.Ordinal);
        var errors = new Dictionary<string, AvroSchemaException>(StringComparer.Ordinal);
        var definedIn = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        for (var progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var file in pending.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var schema = parser.Parse(file.Json);
                    schemas.Add(schema);
                    schemasByPath[file.Path] = schema;
                    foreach (var name in parser.NamedSchemas.Keys.Where(name => !definedIn.ContainsKey(name)).ToList())
                    {
                        definedIn[name] = file.Path;
                    }

                    pending.Remove(file);
                    errors.Remove(file.Path);
                    progress = true;
                }
                catch (AvroSchemaException ex)
                {
                    errors[file.Path] = ex;
                }
            }
        }

        var declaredInFailed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in pending)
        {
            foreach (var name in Declared(file.Json))
            {
                declaredInFailed.TryAdd(name, file.Path);
            }
        }

        return new SchemaFileSet(schemas, schemasByPath, errors, definedIn, declaredInFailed);
    }
}
