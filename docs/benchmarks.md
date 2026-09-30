# Benchmarks

AvroSharp is measured against [Apache.Avro](https://www.nuget.org/packages/Apache.Avro) 1.12.2 with [BenchmarkDotNet](https://benchmarkdotnet.org/), on the same schemas and the same data. Being faster on every benchmark, and allocating no more, is a release gate: [the performance gate](#the-performance-gate) checks it.

On this page:
- [Results](#results)
- [What is measured](#what-is-measured)
- [Running the benchmarks](#running-the-benchmarks)
- [The performance gate](#the-performance-gate)
- [Rules for fast paths](#rules-for-fast-paths)
- [Recorded runs](#recorded-runs)

## Results

The latest full run of the suite: an Intel Core i7-12800H, Windows 11, .NET 10, on the 0.1.1 library code (2026-09-28). **Faster** is Apache.Avro's mean time divided by AvroSharp's: 2.00× means AvroSharp takes half the time. The [full report](reviews/2026-09-28-benchmarks.md) has every case, with times and allocations.

| Area | Faster than Apache.Avro |
|---|---|
| Records, generated code | read **3.95×**, write **6.18×** |
| Records, generic model | read 1.95×, write 3.93× |
| Schema evolution (reading an older schema version) | generated 2.72×, generic 1.32× |
| Container reads (null, deflate, snappy, zstandard) | generated 5.98–7.36×, generic 2.90–3.72× |
| Container reads, xz | generated 22.39×, generic 10.91–11.72× |
| Container reads, bzip2 | 1.63–2.11× |
| Container writes (null, deflate, snappy, zstandard, xz) | generated 5.19–10.35×, generic 3.46–6.44× |
| Container writes, bzip2 | 1.04–1.05× (both libraries compress with SharpZipLib) |
| Showcase scenarios (telemetry, counters, primitive arrays) | read 1.73–4.42×, write 3.77–20.20× |
| Varint decode / encode | 1.42–3.16× / 1.09–4.52× |
| Binary encoding (longs, strings, mixed values) | 1.19–1.98× |
| Bulk long reads | 2.02–3.27× |
| Schema parsing | 1.08–1.58× |

- **Every one of the 94 AvroSharp rows is faster than Apache.Avro, and none allocates more.** Writes allocate close to nothing: under 6 KB for a whole container file, against 5.7 MB for Apache.Avro.
- **Generated code is about twice as fast as the generic model** for records, resolution and container reads.

Changes since that run, measured on the same machine and recorded in the [changelog](../CHANGELOG.md):
- **Primitive arrays in the generic model** are stored as the primitives themselves (#23): reading an array of 1,000 items is 6.6–16.6× faster than Apache.Avro, up from 1.7–2.8×.
- **Schema parsing** of a small schema is 2.07× faster than Apache.Avro, up from 1.08× (#103).
- **Varint encode** of 3-byte values is 1.52× faster than Apache.Avro, up from 1.09× (#102).
- **Wide records** (140 fields): a generated read takes 525 ns, against 729 ns before (#113).

## What is measured

The suite is in [`bench/AvroSharp.Benchmarks`](https://github.com/zcsizmadia/AvroSharp/tree/main/bench/AvroSharp.Benchmarks). Each class compares AvroSharp with Apache.Avro's equivalent API; [Chr.Avro](https://github.com/ch-robinson/dotnet-avro) appears in schema parsing for reference, and is not gated.

| Class | What it measures |
|---|---|
| `GenericRecordBenchmarks` | Writing and reading one order record: AvroSharp's generic and generated code against Apache.Avro's generic datum writer and reader. |
| `WideRecordBenchmarks` | A 140-field record of mostly optional primitives, the shape of many production event schemas. |
| `ShowcaseBenchmarks` | Telemetry and counter records, and arrays of 1,000 ints, longs, doubles and booleans. |
| `ResolutionBenchmarks` | Reading a record written with version 1 of a schema as version 2: a dropped field, promotions, added defaults, reordered enum symbols. |
| `ContainerBenchmarks` | Writing and reading a file of 1,000 orders, per codec (null, deflate, snappy, zstandard, bzip2, xz), synchronous, asynchronous and pipelined. |
| `SchemaParseBenchmarks` | Parsing a small and a large schema, and parsing plus the CRC-64-AVRO fingerprint, as a schema registry client does. |
| `BinaryEncodingBenchmarks` | Encoding and decoding 64K random longs, strings and mixed values with the low-level writer and reader. |
| `VarintBenchmarks` | 64K varints of each encoded length from 1 to 10 bytes, and of random lengths. |
| `BulkReadBenchmarks` | Reading 64K array items in bulk, against Apache.Avro and against AvroSharp's own one-at-a-time loop. |

Both libraries read and write the same logical data. The encoding, varint, bulk-read and showcase benchmarks draw random values with fixed seeds, so every run sees the same bytes and the branch predictor cannot learn a pattern.

## Running the benchmarks

Run from the repository root, on a quiet machine, with the .NET 10 SDK:

```shell
# Everything, on .NET 10
dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*' --memory

# One class, or one method
dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*ContainerBenchmarks*' --memory
dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*GenericRecordBenchmarks.AvroSharp_Read'

# Several runtimes in one run
dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*' --runtimes net8.0 net9.0 net10.0 --memory
```

The arguments after `--` are [BenchmarkDotNet's](https://benchmarkdotnet.org/articles/guides/console-args.html): `--list flat` lists the benchmarks, `--affinity 1` pins the run to one core for steadier numbers, and `--job short` gives a quick, rough answer. Reports go to `BenchmarkDotNet.Artifacts/results`.

## The performance gate

```shell
dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*' --runtimes net8.0 net9.0 net10.0 --memory --gate
```

With `--gate`, the process exits non-zero unless, in every group (class, category, parameters and runtime), each `AvroSharp_*` benchmark is faster than the Apache.Avro baseline **and** allocates no more. A release needs the gate to pass. The gate runs on real hardware, not in CI, where shared runners make timings meaningless.

## Rules for fast paths

A SIMD or bulk path stays only if it beats Apache.Avro on every tested CPU, and AvroSharp's plain scalar loop on current CPUs, on uniform and on mixed data ([#29](https://github.com/zcsizmadia/AvroSharp/issues/29), revised in [#135](https://github.com/zcsizmadia/AvroSharp/issues/135)). A result within 3% of the scalar loop counts as noise; anything slower by more than 3% removes the path. A CPU more than 10 years old may be slower than the loop, but a change never makes a current CPU slower. The paths are measured on x64; there is no Arm64 machine to benchmark on, and CI tests the same paths on Arm64. No path is picked by CPU vendor at startup. The [design notes](design.md) record the decisions and the machines they were measured on: an i7-12800H, an EPYC 7543, an i5-3570K and a Ryzen 5 3500U.

## Recorded runs

Each run that informed a decision is written up, with the machine, the code measured and what it led to. The [documentation index](README.md#reviews-and-measurements) lists them all; the most useful are:

- [Full benchmark run, 2026-09-28](reviews/2026-09-28-benchmarks.md): every benchmark on an i7-12800H with .NET 10.
- [Generated code, 2026-09-26](reviews/2026-09-26-generated-code.md): generated serializers against the generic model and Apache.Avro.
- [Varints and parsing on CPUs with fast PDEP](reviews/2026-09-28-varints-parse-fast-pdep.md) and [without it](reviews/2026-09-28-varints-parse-slow-pdep.md).
- [Performance review, 2026-09-25](reviews/2026-09-25-performance.md): the hot paths against Apache.Avro.
