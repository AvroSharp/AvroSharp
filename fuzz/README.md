# Fuzzing

`AvroSharp.Fuzz` holds coverage-guided fuzz targets for [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer. The targets are in [`FuzzTargets.cs`](AvroSharp.Fuzz/FuzzTargets.cs):

| Target | Input | Checks |
|---|---|---|
| `SchemaParse` | Schema JSON | Only `AvroException` escapes; a parsed schema writes and parses back to the same canonical form |
| `GenericBinary` | First byte picks a schema; the rest is binary data | Only `AvroException` escapes; an accepted value round-trips through binary (exactly) and JSON |
| `GenericJson` | First byte picks a schema; the rest is JSON data | Same as `GenericBinary` |
| `ContainerFile` | An object container file, read with a 1 MiB block limit | Only `AvroException` escapes; every object read round-trips like `GenericBinary` |
| `SingleObject` | A single-object encoded message of one of the schemas | Only `AvroException` escapes; the object round-trips |
| `RegistryMessage` | First byte picks a registry framing (Confluent, Confluent GUID, Apicurio 8-byte, AWS Glue plain and zlib); the rest is the message | Only `AvroException` escapes; the object read round-trips |
| `Resolution` | First byte picks a writer/reader schema pair; the rest is writer data | The resolving reader and the transcoder that generated types use either both fail or give equal values |

The same targets run on every build as a seeded mutation smoke test (`tests/AvroSharp.Tests/Fuzz/FuzzSmokeTests.cs`). That catches crashes close to valid input but is not coverage-guided; use libFuzzer for longer runs.

## Running with libFuzzer

These steps were run on Linux (the `mcr.microsoft.com/dotnet/sdk:10.0` image) with SharpFuzz 2.3.0 and
`libfuzzer-dotnet` v2025.05.02.0904. The weekly workflow (below) runs the same steps.

1. Install the instrumentation tool and download `libfuzzer-dotnet` for your platform from its [releases](https://github.com/Metalnem/libfuzzer-dotnet/releases) (`libfuzzer-dotnet-ubuntu`, `-debian` or `-windows.exe`):

   ```sh
   dotnet tool install --global SharpFuzz.CommandLine
   ```

2. Build the harness and write the seed corpus. Write the seeds **before** instrumenting: instrumented code runs only
   under libFuzzer, and anything else that loads it crashes with an `AccessViolationException`.

   ```sh
   dotnet publish fuzz/AvroSharp.Fuzz -c Release -o out/fuzz
   out/fuzz/AvroSharp.Fuzz --write-seeds corpus
   ```

   In a container whose user does not own the checkout, MinVer cannot read the Git history; add `-p:MinVerSkip=true`.

3. Instrument AvroSharp (only the library under test), then run a target:

   ```sh
   sharpfuzz out/fuzz/AvroSharp.dll
   libfuzzer-dotnet --target_path=out/fuzz/AvroSharp.Fuzz --target_arg=GenericBinary -max_total_time=1800 corpus/GenericBinary
   ```

A crash leaves its input in a `crash-*` file. To reproduce it, add the input to `FuzzSmokeTests` as a fixed case, fix the bug, and keep the case as a regression test.

## Weekly runs

`.github/workflows/fuzz.yml` runs every target for 30 minutes each week (and on demand, with the time as an input), one job per target. Each target's corpus is kept in the Actions cache between runs, so coverage builds up week after week; a crash fails its job and uploads the input as the `crashes-<target>` artifact.

The same workflow runs the random-schema code-generation test (`tests/AvroSharp.Generators.Tests/RandomSchemaCodeGenTests.cs`, #141) on 1,500 batches of 20 schemas; PR CI runs it on 5. Each schema is generated with the source generator, compiled for C# 7.3, C# 12 and the latest C# (and in the Apache.Avro compatibility mode), and its type round-trips random values against the generic reader and writer, through a container file, and from an older version of the schema. A failure prints its seed and schemas, and the failing batch (a file per schema, and the error) is uploaded: as the `random-schema-failure` artifact in the weekly run, and in the `results-<os>` artifact in PR CI (from `AVROSHARP_RANDOM_SCHEMA_FAILURES`). Set `AVROSHARP_RANDOM_SCHEMA_SEED` to the seed (and `AVROSHARP_RANDOM_SCHEMA_BATCHES` for more batches) to reproduce it:

```sh
AVROSHARP_RANDOM_SCHEMA_SEED=<seed> dotnet run --project tests/AvroSharp.Generators.Tests -c Release -f net10.0 -- --treenode-filter "/*/*/RandomSchemaCodeGenTests/*"
```

## Results

| Date | Where | Time per target | Executions per target | Crashes |
|---|---|---|---|---|
| 2026-09-27 | Ryzen 5 3500U, Docker, all 7 targets in parallel | 20 minutes | 4.3–9.1 million | none |
