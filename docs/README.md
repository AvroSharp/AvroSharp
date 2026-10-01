# Documentation

The documentation site is at **[avrosharp.github.io/AvroSharp](https://avrosharp.github.io/AvroSharp/)**, built from these pages and the API's XML documentation.

## Guides

- [Getting started](getting-started/index.md): installing, a first program, and a page per task, each built from a sample: generated types, container files, schema evolution, logical types and JSON.
- [Guide](../README.md): getting started, the generic data model, container files and codecs, single-object messages and schema registries, and streams.
- [Code generation](code-generation.md): the `AvroSharp.Generators` source generator: setup, MSBuild properties, type mapping, schema evolution, diagnostics, and moving from avrogen.
- [Command-line tool](cli.md): `avrosharp gen`, `schema canonical` and `schema fingerprint`, exit codes, and use in CI.
- [Integrations](integrations.md): schema registries and message brokers, AvroSharp.Confluent for Confluent.Kafka, AvroSharp.KafkaFlow for KafkaFlow, and the planned add-on packages for Azure Schema Registry and AWS Glue.
- [Confluent Schema Registry and Kafka](confluent.md): AvroSharp.Confluent's serializers for Confluent.Kafka, their settings, and moving from Confluent's Avro serializer and from Chr.Avro.
- [KafkaFlow](kafkaflow.md): AvroSharp.KafkaFlow's serializer middleware for KafkaFlow, several record types per topic, and moving from KafkaFlow's Confluent Avro serializer.
- [Samples](../samples/README.md): runnable programs for the main APIs, run by CI.

## Reference

- [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html): every public type, from the XML documentation, by namespace: [`AvroSharp.Schemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.html), [`AvroSharp.IO`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.html), [`AvroSharp.Generic`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html), [`AvroSharp.Serialization`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.html), [`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html), [`AvroSharp.Messages`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.html), [`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html), [`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html) and [`AvroSharp.CodeGen`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html).
- [Benchmarks](benchmarks.md): the results against Apache.Avro, what is measured, and how to run the suite and the performance gate.
- [AvroSharp and Apache.Avro](apache-avro.md): what differs, and when Apache.Avro is the better fit.
- [Migrating from Apache.Avro](migrating-from-apache-avro.md): Apache.Avro code and its AvroSharp equivalent, from schemas to generated types, with the compatibility mode and what has no equivalent.
- [Design](design.md): the design proposal, the decisions made since, and how the repository differs from the proposal.
- [Roadmap](roadmap.md): the status of each milestone and where the open work is tracked.
- [Fuzzing](../fuzz/README.md): the libFuzzer targets and how to run them.
- [Contributing](../CONTRIBUTING.md): building, testing, formatting and pull requests.
- [Changelog](../CHANGELOG.md): what changed, newest first.

## Reviews and measurements

Dated notes from reviews and benchmark runs. Each one records what was measured, on which machines, and what it led to.

| Date | Note |
|---|---|
| 2026-09-25 | [Performance review](reviews/2026-09-25-performance.md): the hot paths against Apache.Avro, and the follow-up issues |
| 2026-09-26 | [Benchmarks on 64K data](reviews/2026-09-26-benchmarks-64k.md): 64K random values on an i7-12800H and an EPYC 7543 |
| 2026-09-26 | [Branchless varints](reviews/2026-09-26-branchless-varints.md): branchless varint encoding on the same machines |
| 2026-09-26 | [Hybrid varints](reviews/2026-09-26-hybrid-varints.md): the hybrid varint encoder and its open questions |
| 2026-09-26 | [EPYC varints](reviews/2026-09-26-epyc-varint.md): varint performance on AMD EPYC |
| 2026-09-26 | [Generated code](reviews/2026-09-26-generated-code.md): generated serializers against the generic model and Apache.Avro |
| 2026-09-28 | [Full benchmark run](reviews/2026-09-28-benchmarks.md): every benchmark on an i7-12800H with .NET 10, as speed-ups over Apache.Avro |
| 2026-09-28 | [perf/varints-parse benchmarks](reviews/2026-09-28-varints-parse.md): bulk reads, varint encode and schema parsing on an i5-3570K and a Ryzen 5 3500U |
| 2026-09-28 | [perf/varints-parse at 41d6483](reviews/2026-09-28-varints-parse-rerun.md): the second run on both machines, with the record and container benchmarks |
| 2026-09-28 | [perf/varints-parse on CPUs with fast PDEP](reviews/2026-09-28-varints-parse-fast-pdep.md): the i7-12800H and an EPYC 7543 at 8164630, and the bulk Mixed1-10 fix |
| 2026-09-28 | [perf/varints-parse on CPUs without fast PDEP](reviews/2026-09-28-varints-parse-slow-pdep.md): the i5-3570K and the nas at 8164630, the full suite |
| 2026-09-30 | [Ecosystem integrations spike](reviews/2026-09-30-ecosystem.md): an `AvroSharp.Confluent` prototype on Confluent's serde classes, and which add-on packages to build (#78) |
| 2026-10-01 | [`[AvroSerializable]` types](reviews/2026-10-01-attribute-generator.md): the attribute-driven generator's serializers against generated code, the generic model and Apache.Avro on an EPYC 7543 (#31) |
