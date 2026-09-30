# Migrating from Apache.Avro

This guide maps code written for Apache.Avro (the `Apache.Avro` NuGet package) to AvroSharp. Both libraries implement the same specification, so they read each other's data. Services, and the code within one service, can move one step at a time.

Every example here is taken from the [Migration sample](../samples/Migration/Program.cs). CI builds and runs it against both libraries, and a test checks that each code block below appears in it, so the examples compile against the current API. [AvroSharp and Apache.Avro](apache-avro.md) compares the two libraries. Automated fixes for the mechanical parts are planned in [#151](https://github.com/zcsizmadia/AvroSharp/issues/151).

## The plan

1. **Keep the data as it is.** Nothing about the wire format changes: binary data, container files, single-object messages and schema-registry framing are the same in both libraries. [Bridging through bytes](#bridging-through-bytes) shows how.
2. **Replace avrogen's classes** with the source generator in the Apache.Avro compatibility mode. Code written for the classes compiles unchanged, and Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>` still accept them. See [generated types](#generated-types).
3. **Move call sites** to AvroSharp's APIs, one at a time, using the tables below.
4. **Turn the compatibility mode off, and drop the Apache.Avro reference**, once nothing calls Apache's API. This is the one step that changes types: see [logical types and names](#logical-types-and-names).

## The namespaces

| Apache.Avro | AvroSharp |
|---|---|
| `Avro` (`Schema`, `RecordSchema`, `SchemaNormalization`) | `AvroSharp.Schemas` (`AvroSchema`, `RecordSchema`, fingerprints on the schema) |
| `Avro.Generic` | `AvroSharp.Generic` |
| `Avro.Specific` | Generated types, and `AvroSharp.Serialization` (`AvroSerializer`) |
| `Avro.IO` (`BinaryEncoder`, `BinaryDecoder`) | `AvroSharp.IO` (`AvroWriter`, `AvroReader`), over spans, buffer writers and sequences |
| `Avro.File` (`DataFileWriter<T>`, `DataFileReader<T>`, `Codec`) | `AvroSharp.Containers` (`AvroFileWriter`, `AvroFileReader`, `AvroCodec`); other codecs are in the `AvroSharp.Codecs` package |
| `AvroException`, `SchemaParseException`, `AvroTypeException` | `AvroSharp` (`AvroException`, `AvroSchemaException`, `AvroDataException`) |

## Schemas

Apache.Avro:

```csharp
var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(UserJson);
long apacheFingerprint = Avro.SchemaNormalization.ParsingFingerprint64(apacheSchema);
```

AvroSharp:

```csharp
var schema = (RecordSchema)AvroSchema.Parse(UserJson);
long fingerprint = schema.Fingerprint64;
string canonical = schema.CanonicalForm;
```

- **Fingerprints are the same number.** `Fingerprint64` is CRC-64-AVRO of the Parsing Canonical Form, which is what `SchemaNormalization.ParsingFingerprint64` computes. It's worked out once per schema.
- **Equality:** `Equals` on an AvroSchema is reference equality. `schema.HasSameCanonicalForm(other)` compares encodings.
- **Several files:** parse them together with `new AvroSchemaParser()` and `Parse` once per file, so each file can refer to named types defined in the others. The source generator and the `avrosharp` tool do this for you.

## The generic model

| Apache.Avro | AvroSharp |
|---|---|
| `GenericRecord`, values as `object` | `GenericRecord`, values as `AvroValue`: a 16-byte struct, primitives stored inline without boxing |
| `record["name"]` returns `object`, which you cast | `record["name"]` returns `AvroValue`: `.AsString()`, `.AsInt64()`, `.AsRecord()`, `.IsNull`, `.Kind` |
| `GenericEnum(schema, symbol)`, with `.Value` | `AvroValue.FromEnum(schema, symbol)`, with `.AsEnumSymbol()` and `.AsEnumOrdinal()`. No object is allocated |
| `GenericFixed(schema, bytes)` | `GenericFixed(schema, bytes)` |
| arrays as `object[]` or `IList` | `IReadOnlyList<AvroValue>`; arrays of primitives stay primitive (`TryGetInt64Array` and the others) |
| maps as `IDictionary<string, object>` | `IReadOnlyDictionary<string, AvroValue>` |
| `null` | `AvroValue.Null` |

Writing, with Apache.Avro:

```csharp
var apacheRecord = new Avro.Generic.GenericRecord(apacheSchema);
apacheRecord.Add("id", 42L);
apacheRecord.Add("name", "Ada");
apacheRecord.Add("email", null);
apacheRecord.Add("status", new Avro.Generic.GenericEnum((Avro.EnumSchema)apacheSchema["status"].Schema, "ACTIVE"));
using var apacheOutput = new MemoryStream();
new Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>(apacheSchema).Write(apacheRecord, new Avro.IO.BinaryEncoder(apacheOutput));
byte[] apacheBytes = apacheOutput.ToArray();
```

And with AvroSharp. The bytes are the same:

```csharp
var record = new GenericRecord(schema)
{
    ["id"] = 42L,
    ["name"] = "Ada",
    ["email"] = AvroValue.Null,
    ["status"] = AvroValue.FromEnum((EnumSchema)schema.GetField("status").Schema, "ACTIVE"),
};
byte[] bytes = GenericDatumWriter.Create(schema).WriteToArray(record);
```

Reading, with Apache.Avro:

```csharp
var apacheRead = new Avro.Generic.GenericDatumReader<Avro.Generic.GenericRecord>(apacheSchema, apacheSchema)
    .Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(bytes)));
var apacheName = (string)apacheRead["name"];
var apacheStatus = ((Avro.Generic.GenericEnum)apacheRead["status"]).Value;
```

And with AvroSharp:

```csharp
var read = GenericDatumReader.Create(schema).Read(apacheBytes).AsRecord();
string name = read["name"].AsString();
string status = read["status"].AsEnumSymbol();
bool noEmail = read["email"].IsNull;
```

- **Readers and writers are built once per schema:** `GenericDatumReader.Create` and `GenericDatumWriter.Create` cache them. Keep them rather than creating one per value, as you would keep Apache's.
- **Other ways to read and write:**
  - `Write(IBufferWriter<byte>, value)` and `Read(ReadOnlySpan<byte>)`;
  - `ReadOnlySequence<byte>` overloads;
  - an `AvroWriter`/`AvroReader` over your own buffer.
- **Limits:** the readers limit nesting depth and the number of zero-size items (`GenericDatumReaderOptions`), so hostile input can't exhaust memory or the stack.

## Schema resolution

Apache's `new GenericDatumReader<T>(writerSchema, readerSchema)` becomes:

```csharp
var resolved = GenericDatumReader.Create(schema, newer).Read(apacheBytes).AsRecord();
int tier = resolved["tier"].AsInt32();
```

The rules are the specification's:
- fields match by name or reader alias;
- a missing field takes its default;
- numbers promote (`int` to `long`, `float` or `double`, and the others);
- unknown enum symbols take the reader's enum default.

AvroSharp checks that the schemas can be resolved when the reader is created, not when data first fails. Where Apache.Avro 1.12 departs from the specification, the interop tests record it.

## Container files

Apache's `DataFileWriter<T>` and `DataFileReader<T>` map to `AvroFileWriter` and `AvroFileReader`, and the files are the same format. With Apache.Avro:

```csharp
var apacheFile = new MemoryStream();
using (var apacheWriter = Avro.File.DataFileWriter<Avro.Generic.GenericRecord>.OpenWriter(
    new Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>(apacheSchema), apacheFile, Avro.File.Codec.CreateCodec(Avro.File.Codec.Type.Deflate), leaveOpen: true))
{
    apacheWriter.Append(apacheRecord);
}
```

With AvroSharp:

```csharp
var file = new MemoryStream();
using (var fileWriter = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate, LeaveOpen = true }))
{
    fileWriter.Write(record);
}
```

and reading, here the file Apache.Avro wrote:

```csharp
apacheFile.Position = 0;
using (var fileReader = AvroFileReader.OpenGeneric(apacheFile))
{
    ok &= string.Equals(fileReader.Codec.Name, AvroCodecNames.Deflate, StringComparison.Ordinal) && fileReader.ReadAll().Single().Equals((AvroValue)record);
}
```

| Apache.Avro | AvroSharp |
|---|---|
| `Codec.CreateCodec(Codec.Type.Null)`, `Codec.Type.Deflate` | `AvroCodec.Null`, `AvroCodec.Deflate` (or `new DeflateCodec(level)`) |
| `Apache.Avro.File.Snappy`, `.Zstandard`, `.BZip2`, `.XZ` packages | the `AvroSharp.Codecs` package: `SnappyCodec`, `ZstandardCodec`, `Bzip2Codec`, `XzCodec`. They're managed code, so there's no native zstd library to deploy |
| `reader.Next()` / `HasNext()` | `ReadAll()`, `ReadAllAsync()`, or `ReadAllPipelinedAsync()`, which decompresses the next blocks in the background |
| `GetMetaString(key)` | `TryGetMetadataString(key, out value)`, and `Metadata` |
| `Sync()`, `PastSync()`, `Seek()` | the same, for splitting a file between readers |

The readers bound block sizes and metadata, so a corrupt or hostile file fails with an `AvroDataException`, not an out-of-memory error. Async writing and reading need no synchronous I/O.

## Generated types

In the Apache.Avro compatibility mode, the source generator replaces avrogen. The project file needs:

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" />
  <PackageReference Include="Apache.Avro" />
  <AdditionalFiles Include="Schemas\*.avsc" />
</ItemGroup>
<PropertyGroup>
  <AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>
</PropertyGroup>
```

The generated class has avrogen's shape: the same property names (the Avro field names), the static `_SCHEMA` and the instance `Schema`, `Get`/`Put`, and Apache's .NET types for logical types. So Apache's specific API keeps working:

```csharp
var user = new migration.User { id = 42, name = "Ada", email = null, status = migration.Status.ACTIVE, joined = new DateTime(2020, 1, 15) };

using var specificOutput = new MemoryStream();
new SpecificDatumWriter<migration.User>(migration.User._SCHEMA).Write(user, new Avro.IO.BinaryEncoder(specificOutput));
```

AvroSharp's serializers are on the same class, and write the same bytes:

```csharp
byte[] userBytes = user.ToAvroBytes();
var sameUser = migration.User.FromAvroBytes(specificOutput.ToArray());
```

| Apache.Avro | AvroSharp |
|---|---|
| `SpecificDatumWriter<T>` + `BinaryEncoder` + `Write` | `value.ToAvroBytes()`, `TryWriteAvroBytes(Span<byte>, out n)`, `T.Write(ref writer, value)` |
| `SpecificDatumReader<T>(writer, reader)` + `BinaryDecoder` + `Read` | `T.FromAvroBytes(bytes)`, or `T.FromAvroBytes(bytes, writerSchema)` to resolve another version |
| `DataFileWriter<T>` of a specific type | `AvroFileWriter.Create<T>(stream)` (.NET 8 and later), or `AvroFileWriter.Create(stream, T.AvroSharpSchema, T.Write)` |
| avrogen, run by hand | the build, or the [`avrosharp` tool](cli.md#coming-from-avrogen) with `--apache-compatible` for checked-in code |

For generated code there's no reflection and no boxing, and the generated readers resolve older schema versions directly. [Code generation](code-generation.md) describes the generator and its options.

## Logical types and names

Turning the compatibility mode off drops the Apache.Avro types from the generated classes. That changes some property types and, by default, the property names:

| Schema | Compatibility mode (Apache's types) | AvroSharp's own types |
|---|---|---|
| `date` | `DateTime` | `DateOnly` (`DateTime` on .NET Standard) |
| `time-millis`, `time-micros` | `TimeSpan` | `TimeOnly` (`TimeSpan` on .NET Standard) |
| `timestamp-millis`, `timestamp-micros` | `DateTime` (UTC) | `DateTimeOffset` |
| `local-timestamp-millis`, `local-timestamp-micros` | `DateTime` | `DateTime` (unspecified kind) |
| `uuid` on `string` | `Guid` | `Guid` |
| `decimal` | `Avro.AvroDecimal` | `decimal`, up to precision 28; exact, or rejected, never rounded |
| property names | the Avro field names (`order_id`) | PascalCase (`OrderId`); `AvroSharpPropertyNames=avro` keeps the field names |

Code that uses these properties has to be checked by hand. It usually still compiles, but a `DateTime` that becomes a `DateOnly`, or a UTC `DateTime` that becomes a `DateTimeOffset`, is a decision, not a rename. `AvroSharpLogicalTypes=raw` keeps the underlying Avro types (`int`, `long`, `bytes`) instead.

## Exceptions

| Apache.Avro | AvroSharp |
|---|---|
| `SchemaParseException` | `AvroSchemaException`, with the JSON path, line and column of the problem |
| `AvroTypeException`, `AvroException` while reading | `AvroDataException`, for malformed or truncated data |
| `AvroException` while writing | `AvroException`, naming the field |

```csharp
try
{
    AvroSchema.Parse("""{"type":"record","name":"Broken"}""");
    ok = false;
}
catch (AvroSchemaException ex)
{
    Console.WriteLine($"Exceptions: an invalid schema is an AvroSchemaException: {ex.Message}");
}
```

All three derive from `AvroException`.

## Bridging through bytes

The two libraries don't need to reference each other: both read and write the same bytes. So a service can move while its peers stay on Apache.Avro, and one codebase can use both during the move. Every section above shows one library reading what the other wrote. The interop tests check this in both directions, for random schemas and values, and for container files with every codec.

## What has no direct equivalent

- **Apache's reflect API**, which derives schemas from existing classes. AvroSharp generates classes from schemas. An attribute-driven generator is planned (#31).
- **Protocols and RPC** (`.avpr`, `Avro.ipc`) aren't supported (#33).
- **Stream-based encoders:** `BinaryEncoder`/`BinaryDecoder` over a `Stream` become `AvroWriter`/`AvroReader` over a span, an `IBufferWriter<byte>` or a `ReadOnlySequence<byte>`. `AvroStreamWriter`/`AvroStreamReader` handle a stream of objects without a container. A loop that reads a stream value by value needs rewriting, usually around a pipe or a container file.
- **Custom codecs:** derive from `AvroCodec`. That's the same idea as Apache's `Codec`, with a different signature (`ReadOnlyMemory<byte>` in, `IBufferWriter<byte>` out).
- **Single-object messages and schema registries:** Apache.Avro for C# has no single-object encoding. AvroSharp's `AvroMessage` and `AvroRegistryMessage` (Confluent, Apicurio, AWS Glue) are new, not replacements.
