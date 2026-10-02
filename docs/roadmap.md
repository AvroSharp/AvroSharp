# Roadmap

The live status of each milestone. The milestones and their exit criteria come from [design.md §11](design.md#11-phased-roadmap-each-milestone-ends-with-a-tagged-pre-release); the GitHub milestones and issues are the source of truth for open work. Last updated 2026-10-02.

| Milestone | Status | Open work |
|---|---|---|
| M0: skeleton and CI | Done | The packages are published to nuget.org from the maintainer's account, since 0.1.1. |
| M1: schemas | Done | |
| M2: binary encoding and the generic model | Done | |
| M2.5: code generation from `.avsc` files | Done | Union classes (#14), `required`/`init` (#15), cyclic cross-file references (#16), options (#18), JSON for generated types (#19) |
| M3: schema resolution | Done | |
| M4: attribute generator and `AvroSerializer<T>` | Done (1.0.0-rc.1) | #31: `[AvroSerializable]` and `AvroTypes`, designed in #180 and built in #181. Later: `init`-only members, positional records, nested types, collection interfaces ([design §6.5.5](design.md#655-scope-of-the-first-version-and-what-comes-later)) |
| M5: container files, codecs, single-object encoding | Done | |
| M6: CLI tool and protocols | Started | The `avrosharp` tool (`gen`, `schema canonical`, `schema fingerprint`) ships in 0.2.0, and `schema compat` in 1.0.0-rc.1 (#165); the file commands (#172) and protocols are open (#33) |
| M7: hardening and 1.0 | Started | 1.0.0-rc.1 was released on 2026-10-01, with the core packages' public API frozen. The next release is 1.0.0, with no rc.2; what's left is in the release checklist (#216). The add-on packages have never been published, and go straight to 1.0.0 with the core packages; their APIs may still change until then. 1.0.0 ships before .NET 11; the .NET 11 follow-ups come after it (#222), and additive work is in the 1.x milestone. Done: the docs site and samples (#74, #126), the migration guide (#21), the ecosystem spike (#78), the performance gate fix (#157), package metadata (#66), the package icon and logo (#122), coverage (#75), release workflows (#76), nightly fuzzing (#34), public API before 1.0 (#134), the API freeze and package validation baseline (#73), CI consuming the packages (#136), dev container (#139), performance review (#135), release hardening (#35) |
| Integrations | Started | #77, [integrations](integrations.md): `AvroSharp.Confluent` (#185), `AvroSharp.KafkaFlow` (#154), `AvroSharp.Azure.SchemaRegistry` (#155) and `AvroSharp.Aws.Glue` with `AvroSharp.Aws.Glue.Kafka` (#156) are new in 1.0.0. The add-on packages live in this repository and are released with AvroSharp at the same version (#77). Their open follow-ups are in the 1.x milestone: AvroSharp.Confluent's among #186 to #203, such as field rules (#186) and migration rules (#187), and the test and interop gaps (#215). More add-ons are #77. Done: schema references (#80), registry wire framing (#81), the field walker (#82), the spike (#78), the package policy (#83), CEL rules by Avro field names (#191), and the review follow-ups (#211 to #214) |

## Done, in more detail

- **Schemas:** parsing (System.Text.Json), writing, Parsing Canonical Form, CRC-64-AVRO/MD5/SHA-256 fingerprints, all Avro 1.12 logical types. Checked against Apache's `schema-tests.txt`.
- **Encoding:** [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html)/[`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) over spans, `IBufferWriter<byte>` and `ReadOnlySequence<byte>`. The generic model ([`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html), [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html)) is binary and JSON, with schema resolution.
- **Code generation:** records, enums and fixed types with direct serializers, the Apache.Avro compatibility mode, logical types, and resolution for generated types, from the source generator or the `avrosharp` tool.
- **Files and messages:**
  - schema-registry wire framing (Confluent, Apicurio, AWS Glue) and schema references;
  - container files, sync and async, with seeking and splitting;
  - every codec: `null` and `deflate` built in, and snappy, zstandard, bzip2 and xz in `AvroSharp.Codecs`;
  - single-object encoding;
  - streams of objects without a container ([`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html)).
- **Verification:**
  - property tests against Apache.Avro C#;
  - files written by Apache Avro Java for every codec;
  - fuzz targets, run as a smoke test on every build;
  - a Native AOT smoke test.
- **Hardening (0.2.0):** bounded nesting and memory for hostile schemas and data (#129), resolution that follows Java where the specification leaves a choice (#130), code generation fixes from a review with random names and defaults (#131), and ordered assertions, coverage checks, and Java- and Glue-written reference data in the tests (#133).

See [CHANGELOG.md](../CHANGELOG.md) for the details of each change.
