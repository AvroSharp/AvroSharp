using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvroSharp.Schemas;

/// <summary>
/// Parses Avro schema JSON. A parser remembers the named schemas it has parsed, so later schemas
/// (for example other <c>.avsc</c> files) can refer to them by name.
/// </summary>
/// <remarks>
/// Parsing is atomic: if a schema is invalid, none of its named types are added to <see cref="NamedSchemas"/>.
/// Instances are not thread-safe.
/// </remarks>
/// <seealso cref="AvroSchemaParseOptions"/>
/// <seealso cref="AvroSchema"/>
public sealed class AvroSchemaParser
{
    private readonly Dictionary<string, NamedSchema> _namedSchemas = new(StringComparer.Ordinal);

    /// <summary>Initializes a parser.</summary>
    /// <param name="options">Parse options, or <see langword="null"/> for the defaults.</param>
    public AvroSchemaParser(AvroSchemaParseOptions? options = null)
    {
        Options = options ?? AvroSchemaParseOptions.Default;
    }

    /// <summary>Gets the parse options.</summary>
    public AvroSchemaParseOptions Options { get; }

    /// <summary>Gets the named schemas known to this parser, by full name.</summary>
    public IReadOnlyDictionary<string, NamedSchema> NamedSchemas => _namedSchemas;

    /// <summary>
    /// Makes a named schema, and every named schema it contains, available to schemas parsed later.
    /// </summary>
    /// <param name="schema">The schema to add.</param>
    /// <exception cref="AvroSchemaException">A different schema with the same full name is already known.</exception>
    public void AddNamedSchemas(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var found = new Dictionary<string, NamedSchema>(StringComparer.Ordinal);
        Collect(schema, found);
        foreach (var named in found.Values)
        {
            if (_namedSchemas.TryGetValue(named.FullName, out var existing) && !ReferenceEquals(existing, named))
            {
                throw new AvroSchemaException($"A different schema named '{named.FullName}' is already defined.");
            }
        }

        foreach (var named in found.Values)
        {
            _namedSchemas[named.FullName] = named;
        }

        static void Collect(AvroSchema schema, Dictionary<string, NamedSchema> found)
        {
            switch (schema)
            {
                case NamedSchema named when found.ContainsKey(named.FullName):
                    return;
                case RecordSchema record:
                    found.Add(record.FullName, record);
                    foreach (var field in record.Fields)
                    {
                        Collect(field.Schema, found);
                    }

                    break;
                case NamedSchema named:
                    found.Add(named.FullName, named);
                    break;
                case ArraySchema array:
                    Collect(array.Items, found);
                    break;
                case MapSchema map:
                    Collect(map.Values, found);
                    break;
                case UnionSchema union:
                    foreach (var branch in union.Branches)
                    {
                        Collect(branch, found);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Parses a schema from JSON text.</summary>
    /// <param name="json">The schema JSON.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public AvroSchema Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(json, 0, json.Length, buffer, 0);
            return ParseCore(buffer.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Parses a schema from UTF-8 encoded JSON.</summary>
    /// <param name="utf8Json">The schema JSON as UTF-8 bytes.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public AvroSchema Parse(ReadOnlySpan<byte> utf8Json)
    {
        // The pooled JsonDocument reads from memory, not a span; schemas are small, so the copy is cheap.
        var buffer = ArrayPool<byte>.Shared.Rent(utf8Json.Length);
        try
        {
            utf8Json.CopyTo(buffer);
            return ParseCore(buffer.AsMemory(0, utf8Json.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // PERF (#103): the JSON is parsed into a pooled JsonDocument, which is returned when parsing ends; the defaults
    // and custom properties a schema keeps are cloned out of it. JsonElement.ParseValue, which owns its memory, cost
    // about 2 us more for a small schema, a fixed cost of every parse.
    private AvroSchema ParseCore(ReadOnlyMemory<byte> utf8Json)
    {
        var readerOptions = new JsonReaderOptions
        {
            CommentHandling = Options.AllowComments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = Options.AllowComments,
            MaxDepth = Options.MaxDepth,
        };

        if (!Utf8Validation.IsValid(utf8Json.Span))
        {
            throw new AvroSchemaException("The schema JSON is not valid UTF-8.", path: null, lineNumber: null, bytePositionInLine: null);
        }

        JsonDocument document;
        try
        {
            // Rejects content after the schema, too.
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                CommentHandling = readerOptions.CommentHandling,
                AllowTrailingCommas = readerOptions.AllowTrailingCommas,
                MaxDepth = readerOptions.MaxDepth,
            });
        }
        catch (JsonException ex)
        {
            throw new AvroSchemaException(
                $"The schema is not valid JSON: {ex.Message}",
                path: null,
                lineNumber: ex.LineNumber + 1,
                bytePositionInLine: ex.BytePositionInLine + 1,
                ex);
        }

        using (document)
        {
            var builder = new SchemaJsonReader(Options, _namedSchemas);
            try
            {
                var schema = builder.Read(document.RootElement);
                builder.Commit();
                return schema;
            }
            catch (SchemaJsonReader.ParseError error)
            {
                long? line = null;
                long? column = null;
                if (JsonLocator.TryLocate(utf8Json.Span, readerOptions, error.Path, out var l, out var c))
                {
                    line = l;
                    column = c;
                }

                throw new AvroSchemaException(error.Reason, JsonLocator.FormatPath(error.Path), line, column, error.InnerException);
            }
        }
    }

    /// <summary>Reads and parses a schema from a stream of UTF-8 encoded JSON.</summary>
    /// <param name="utf8Json">The stream to read; it is not disposed.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public async ValueTask<AvroSchema> ParseAsync(Stream utf8Json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        // Schemas are small; buffering the whole document keeps parsing synchronous and allows
        // error locations (line and column) to be reported.
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var read = await utf8Json.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return ParseCore(buffer.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
