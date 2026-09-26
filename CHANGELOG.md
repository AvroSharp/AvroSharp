# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

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

- Invalid UTF-8 inside a JSON string (schema JSON or JSON data) raised `InvalidOperationException` instead of `AvroSchemaException`/`AvroDataException`. Found by the fuzz smoke test.
