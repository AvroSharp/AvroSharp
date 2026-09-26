# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Schema resolution for generated types: `Read(ref reader, writerSchema)` and `FromAvroBytes(data, writerSchema)` read data written with another version of the type's schema. Data of the same canonical schema takes the direct path; other data is resolved with the generic resolving reader first. A resolution plan emitted into the generated code is a planned optimization.
- Schema resolution for the generic model: `GenericDatumReader.Create(writerSchema, readerSchema)` reads data written with one schema version as another, following the specification. Record fields are matched by name or alias, writer-only fields are skipped (sized blocks in one step), and missing reader fields take their defaults. Named types match by full name, unqualified name or alias. Numbers are promoted, `string` and `bytes` convert, and enum symbols are matched with the reader's default for unknown ones. Unions resolve per branch. Incompatible schemas are rejected when the reader is created; a mismatch that only some data would hit (a union branch, an unknown enum symbol without a default) is reported when such a value is read.
- `AvroSchemaParseOptions.AllowIdenticalRedefinitions`: a parser may accept a named type that an earlier `Parse` call defined, when both definitions have the same canonical form; the first definition stays in use. The source generator turns it on, so schema sets that inline shared types in every file (as Apache's one-file-at-a-time tooling requires) generate each type once. Different definitions are still an error that names both files.
- Generator option `AvroSharpPropertyNames=avro` (`CodeGenOptions.PropertyNaming`): keep the Avro field names as property names, as Apache's `avrogen` does, so code written against avrogen classes compiles unchanged. C# keywords are escaped.
- Apache.Avro compatibility mode for generated code (`AvroSharpApacheCompatible=true`, requires a reference to Apache.Avro): records also implement `Avro.Specific.ISpecificRecord`, fixed types derive from `Avro.Specific.SpecificFixed`, and logical types use Apache's .NET types, so Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>` and AvroSharp's serializers work on the same classes and produce the same bytes. `Put` also accepts what Apache's reader passes: an enum's ordinal, and an `AvroDecimal` for a decimal on fixed. The generator reports `AVROGEN004` when the property is set without the reference.
- Logical types in generated code:
  - `date` becomes `DateOnly` and `time-millis`/`time-micros` become `TimeOnly` (`DateTime`/`TimeSpan` where those types don't exist);
  - `timestamp-millis`/`timestamp-micros` become `DateTimeOffset`, and the `local-timestamp` variants become `DateTime`;
  - `uuid` (on `string` or `fixed(16)`) becomes `Guid`;
  - `decimal` with a precision up to 28 becomes `decimal`.
  - Set the MSBuild property `AvroSharpLogicalTypes=raw` to keep the underlying types.
  - The conversions are public in `AvroLogicalValues`:
    - decimals are exact, raising an error instead of rounding;
    - times and timestamps are truncated towards negative infinity to the logical type's precision;
    - out-of-range data raises `AvroDataException`.
- Generated records implement `IAvroSpecificRecord`: `Schema`, `Get(int)` and `Put(int, object?)`, field access by position following the contract of Apache.Avro's `ISpecificRecord` without depending on it. `Put` checks the value's type (no implicit widening, as with Apache's casts) and names the field in errors.
- Code generation from schema files: the `AvroSharp.Generators` source generator (an incremental generator for `.avsc` files passed as `AdditionalFiles`) and the `AvroSharp.CodeGen` engine it uses. Records become partial classes with static `Write`/`Read` methods (plus `ToAvroBytes`/`FromAvroBytes`) that call `AvroWriter`/`AvroReader` directly in schema order; enums become C# enums; fixed types become size-checked wrappers. Schema files may refer to each other's named types; errors are reported at the file, line and column. Generated readers enforce the same hostile-input limits as the generic reader. The generator needs the .NET 10 SDK or Visual Studio 2026.
- JSON encoding for the generic model: `GenericDatumJsonWriter` and `GenericDatumJsonReader`, following the specification (wrapped union values, byte strings for `bytes` and `fixed`, enum symbols). Record fields may appear in any order and missing fields take their defaults; NaN and infinities are written as strings, as Apache.Avro C# does. Checked both ways against Apache.Avro's `JsonEncoder`/`JsonDecoder`.
- Fuzz targets (`fuzz/AvroSharp.Fuzz`, SharpFuzz/libFuzzer) for schema parsing and generic binary and JSON data, each also checking round trips. They run on every build as a seeded mutation smoke test.
- Generic data model (`AvroSharp.Generic`): `AvroValue`, a 16-byte struct holding any Avro value without boxing (primitives inline, enums as schema plus ordinal), `GenericRecord` and `GenericFixed`. `GenericDatumWriter` and `GenericDatumReader` compile a schema once into a cached, thread-safe plan of typed nodes; union branches are selected from the value's kind or schema name. Arrays of `int`/`long`/`float`/`double` are read in bulk. Hostile input is bounded: block counts are checked against the remaining input (using each record's minimum encoded size), pre-allocation is capped, zero-size items draw from a per-read budget, and record nesting is limited when reading and writing (`GenericDatumReaderOptions`, `GenericDatumWriterOptions`).
- `AvroReader.ReadLongs`/`ReadInts`: bulk varint reads; on net8+ a `Vector128` check decodes runs of one-byte values 16 at a time.
- Binary encoding (`AvroSharp.IO`): `AvroWriter` writes directly into an `IBufferWriter<byte>` or a `Span<byte>`; `AvroReader` reads from a `ReadOnlySpan<byte>` or a multi-segment `ReadOnlySequence<byte>`, returning slices of the input for `bytes`, `string` and `fixed` when contiguous. Bulk `double`/`float` array items are a single copy on little-endian hardware. Length prefixes are checked against the remaining input before any allocation; malformed data raises `AvroDataException`.
- Schema model (`AvroSharp.Schemas`): immutable primitive, record, enum, array, map, union and fixed schemas; names, namespaces and aliases; record fields with defaults, order and aliases; custom properties.
- All Avro 1.12 logical types. Unknown or invalid logical types are ignored and kept as properties, as the specification requires.
- `AvroSchemaParser` and `AvroSchema.Parse`/`ParseAsync`: `System.Text.Json` parser over UTF-8 with default-value validation, optional comments, and errors that report the JSON path, line and column. A parser keeps named types across calls, so schemas split over several files can refer to each other.
- Full schema JSON writer, Parsing Canonical Form, and CRC-64-AVRO, MD5 and SHA-256 fingerprints.
- Tests against Apache Avro's `schema-tests.txt` vectors, property-based interop tests against Apache.Avro (C#), a Native AOT smoke test, and schema-parse benchmarks gated against Apache.Avro.
- Repository skeleton: build settings, analyzers, public API tracking, strong naming, TUnit tests on .NET 8/9/10 and .NET Framework 4.8.1, and CI on Linux and Windows (x64 and Arm64).
- `AvroCodecNames`: the codec names defined by the specification.

### Fixed

- Generated code needed C# 9 (`new()` initializers, `??=`, `is { }` and `is not` patterns), so it failed to compile in netstandard2.0 and .NET Framework projects, which default to C# 7.3. It now uses constructs every version accepts, and emits nullable annotations only for C# 8 and later.
- Invalid UTF-8 inside a JSON string (schema JSON or JSON data) raised `InvalidOperationException` instead of `AvroSchemaException`/`AvroDataException`. Found by the fuzz smoke test.
