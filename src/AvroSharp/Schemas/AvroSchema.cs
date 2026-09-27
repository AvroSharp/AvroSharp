using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvroSharp.Schemas;

/// <summary>
/// An immutable Avro schema. Use <see cref="Parse(string, AvroSchemaParseOptions?)"/> to read schema JSON,
/// or construct the concrete schema types directly.
/// </summary>
public abstract class AvroSchema
{
    private static readonly IReadOnlyDictionary<string, JsonElement> s_noProperties = new Dictionary<string, JsonElement>(0, StringComparer.Ordinal);

    private CanonicalData? _canonical;

    // The last schema found to have this schema's canonical form, so that repeated checks against it (once per record
    // when reading generated types from a file or messages) compare references instead of the canonical text.
    private AvroSchema? _sameCanonicalForm;

    private protected AvroSchema(AvroSchemaType type, AvroLogicalType? logicalType, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        Type = type;
        LogicalType = logicalType;
        Properties = properties is null || properties.Count == 0 ? s_noProperties : properties;
    }

    /// <summary>Gets the <c>null</c> schema.</summary>
    public static PrimitiveSchema Null { get; } = new(AvroSchemaType.Null);

    /// <summary>Gets the <c>boolean</c> schema.</summary>
    public static PrimitiveSchema Boolean { get; } = new(AvroSchemaType.Boolean);

    /// <summary>Gets the <c>int</c> schema.</summary>
    public static PrimitiveSchema Int { get; } = new(AvroSchemaType.Int);

    /// <summary>Gets the <c>long</c> schema.</summary>
    public static PrimitiveSchema Long { get; } = new(AvroSchemaType.Long);

    /// <summary>Gets the <c>float</c> schema.</summary>
    public static PrimitiveSchema Float { get; } = new(AvroSchemaType.Float);

    /// <summary>Gets the <c>double</c> schema.</summary>
    public static PrimitiveSchema Double { get; } = new(AvroSchemaType.Double);

    /// <summary>Gets the <c>bytes</c> schema.</summary>
    public static PrimitiveSchema Bytes { get; } = new(AvroSchemaType.Bytes);

    /// <summary>Gets the <c>string</c> schema.</summary>
    public static PrimitiveSchema String { get; } = new(AvroSchemaType.String);

    /// <summary>Gets the schema type.</summary>
    public AvroSchemaType Type { get; }

    /// <summary>Gets the logical type annotating this schema, or <see langword="null"/>.</summary>
    public AvroLogicalType? LogicalType { get; }

    /// <summary>
    /// Gets the attributes that are not defined by the specification for this kind of schema
    /// (custom metadata, and the attributes of an unknown or invalid logical type).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; }

    /// <summary>
    /// Gets the Parsing Canonical Form of this schema, as defined by the specification. Two schemas with the
    /// same canonical form read and write the same binary data.
    /// </summary>
    public string CanonicalForm => GetCanonical().Text;

    /// <summary>Gets the CRC-64-AVRO (Rabin) fingerprint of the <see cref="CanonicalForm"/>.</summary>
    public long Fingerprint64 => GetCanonical().Fingerprint;

    /// <summary>Parses a schema from JSON text.</summary>
    /// <param name="json">The schema JSON.</param>
    /// <param name="options">Parse options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public static AvroSchema Parse(string json, AvroSchemaParseOptions? options = null) =>
        new AvroSchemaParser(options).Parse(json);

    /// <summary>Parses a schema from UTF-8 encoded JSON.</summary>
    /// <param name="utf8Json">The schema JSON as UTF-8 bytes.</param>
    /// <param name="options">Parse options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public static AvroSchema Parse(ReadOnlySpan<byte> utf8Json, AvroSchemaParseOptions? options = null) =>
        new AvroSchemaParser(options).Parse(utf8Json);

    /// <summary>Reads and parses a schema from a stream of UTF-8 encoded JSON.</summary>
    /// <param name="utf8Json">The stream to read; it is not disposed.</param>
    /// <param name="options">Parse options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <exception cref="AvroSchemaException">The JSON is malformed or the schema is invalid.</exception>
    public static ValueTask<AvroSchema> ParseAsync(Stream utf8Json, AvroSchemaParseOptions? options = null, CancellationToken cancellationToken = default) =>
        new AvroSchemaParser(options).ParseAsync(utf8Json, cancellationToken);

    /// <summary>Writes the full schema JSON, including documentation, aliases, defaults and custom properties.</summary>
    /// <param name="indented"><see langword="true"/> to indent the output.</param>
    public string ToJson(bool indented = false) => SchemaJsonWriter.ToJson(this, indented);

    /// <summary>Writes the full schema JSON to <paramref name="writer"/>.</summary>
    /// <param name="writer">The destination.</param>
    public void WriteTo(Utf8JsonWriter writer) => SchemaJsonWriter.Write(this, writer);

    /// <summary>Returns the full schema JSON.</summary>
    public override string ToString() => ToJson();

    internal byte[] GetCanonicalUtf8() => GetCanonical().Utf8;

    // Benign race: concurrent callers compute identical values and one reference wins.
    private CanonicalData GetCanonical() => _canonical ??= CanonicalData.Create(this);

    /// <summary>Gets whether <paramref name="other"/> has this schema's Parsing Canonical Form (and so the same encoding).</summary>
    internal bool HasSameCanonicalForm(AvroSchema other)
    {
        if (ReferenceEquals(this, other) || ReferenceEquals(_sameCanonicalForm, other))
        {
            return true;
        }

        if (Fingerprint64 != other.Fingerprint64 || !string.Equals(CanonicalForm, other.CanonicalForm, StringComparison.Ordinal))
        {
            return false;
        }

        _sameCanonicalForm = other;
        return true;
    }

    private sealed class CanonicalData
    {
        private CanonicalData(byte[] utf8, string text, long fingerprint)
        {
            Utf8 = utf8;
            Text = text;
            Fingerprint = fingerprint;
        }

        public byte[] Utf8 { get; }

        public string Text { get; }

        public long Fingerprint { get; }

        public static CanonicalData Create(AvroSchema schema)
        {
            var utf8 = CanonicalFormWriter.Write(schema);
            return new CanonicalData(utf8, System.Text.Encoding.UTF8.GetString(utf8), SchemaFingerprint.Crc64Avro(utf8));
        }
    }
}
