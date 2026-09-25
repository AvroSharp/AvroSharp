# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Binary encoding (`AvroSharp.IO`): `AvroWriter` writes directly into an `IBufferWriter<byte>` or a `Span<byte>`; `AvroReader` reads from a `ReadOnlySpan<byte>` or a multi-segment `ReadOnlySequence<byte>`, returning slices of the input for `bytes`, `string` and `fixed` when contiguous. Bulk `double`/`float` array items are a single copy on little-endian hardware. Length prefixes are checked against the remaining input before any allocation; malformed data raises `AvroDataException`.
- Schema model (`AvroSharp.Schemas`): immutable primitive, record, enum, array, map, union and fixed schemas; names, namespaces and aliases; record fields with defaults, order and aliases; custom properties.
- All Avro 1.12 logical types. Unknown or invalid logical types are ignored and kept as properties, as the specification requires.
- `AvroSchemaParser` and `AvroSchema.Parse`/`ParseAsync`: `System.Text.Json` parser over UTF-8 with default-value validation, optional comments, and errors that report the JSON path, line and column. A parser keeps named types across calls, so schemas split over several files can refer to each other.
- Full schema JSON writer, Parsing Canonical Form, and CRC-64-AVRO, MD5 and SHA-256 fingerprints.
- Tests against Apache Avro's `schema-tests.txt` vectors, property-based interop tests against Apache.Avro (C#), a Native AOT smoke test, and schema-parse benchmarks gated against Apache.Avro.
- Repository skeleton: build settings, analyzers, public API tracking, strong naming, TUnit tests on .NET 8/9/10 and .NET Framework 4.8.1, and CI on Linux and Windows (x64 and Arm64).
- `AvroCodecNames`: the codec names defined by the specification.
