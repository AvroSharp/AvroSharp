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
    private SchemaFileSet(IReadOnlyList<AvroSchema> schemas, IReadOnlyDictionary<string, AvroSchema> schemasByPath, IReadOnlyDictionary<string, AvroSchemaException> errors, IReadOnlyDictionary<string, string> definedIn)
    {
        Schemas = schemas;
        SchemasByPath = schemasByPath;
        Errors = errors;
        DefinedIn = definedIn;
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
    /// defines differently, the message names that other file.
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

        return message;
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

        return new SchemaFileSet(schemas, schemasByPath, errors, definedIn);
    }
}
