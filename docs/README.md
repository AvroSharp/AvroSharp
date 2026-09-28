# Documentation

- [Roadmap](roadmap.md): the status of each milestone and where the open work is tracked.
- [Design](design.md): the design proposal, the decisions made since, and how the repository differs from the proposal.
- [Fuzzing](../fuzz/README.md): the libFuzzer targets and how to run them.
- [Samples](../samples/README.md): runnable programs for the main APIs, run by CI.
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
