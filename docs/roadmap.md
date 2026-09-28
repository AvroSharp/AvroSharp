# Roadmap

The live status of each milestone. The milestones and their exit criteria come from [design.md §11](design.md#11-phased-roadmap-each-milestone-ends-with-a-tagged-pre-release); the GitHub milestones and issues are the source of truth for open work. Last updated 2026-09-27.

| Milestone | Status | Open work |
|---|---|---|
| M0: skeleton and CI | Done | No package is published yet (#84). |
| M1: schemas | Done | |
| M2: binary encoding and the generic model | Done | SIMD for one-byte varint runs (#24), decided by the SIMD rule (#29) |
| M2.5: code generation from `.avsc` files | Done | Union classes (#14), `required`/`init` (#15), cyclic cross-file references (#16), options (#18), JSON for generated types (#19), SDK requirement (#20) |
| M3: schema resolution | Done | A resolution plan per writer schema for generated readers (#69) |
| M4: attribute generator and `AvroSerializer<T>` | Not started | #31 |
| M5: container files, codecs, single-object encoding | Mostly done | Pipelined reading through Channels; the benchmark gate for every codec (#32) |
| M6: CLI tool and protocols | Not started | #33 |
| M7: hardening and 1.0 | Started | #35: API review (#73), package metadata (#66), docs site (#74), coverage (#75), release workflows (#76), fuzzing nightly (#34) |
| Integrations | Started | #77: Confluent (#79). Done: schema references (#80) and registry wire framing (#81). Remaining core work: the field walker (#82) and the typed serializer lookup (#31) |

## Done, in more detail

- **Schemas:** parsing (System.Text.Json), writing, Parsing Canonical Form, CRC-64-AVRO/MD5/SHA-256 fingerprints, all Avro 1.12 logical types. Checked against Apache's `schema-tests.txt`.
- **Encoding:** `AvroWriter`/`AvroReader` over spans, `IBufferWriter<byte>` and `ReadOnlySequence<byte>`. The generic model (`AvroValue`, `GenericRecord`) is binary and JSON, with schema resolution.
- **Code generation:** records, enums and fixed types with direct serializers, the Apache.Avro compatibility mode, logical types, and resolution for generated types.
- **Files and messages:**
  - schema-registry wire framing (Confluent, Apicurio, AWS Glue) and schema references;
  - container files, sync and async, with seeking and splitting;
  - every codec: `null` and `deflate` built in, and snappy, zstandard, bzip2 and xz in `AvroSharp.Codecs`;
  - single-object encoding;
  - streams of objects without a container (`AvroSharp.Streams`).
- **Verification:**
  - property tests against Apache.Avro C#;
  - files written by Apache Avro Java for every codec;
  - fuzz targets, run as a smoke test on every build;
  - a Native AOT smoke test.

See [CHANGELOG.md](../CHANGELOG.md) for the details of each change.
