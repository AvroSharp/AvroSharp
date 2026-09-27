# Fuzzing

`AvroSharp.Fuzz` holds coverage-guided fuzz targets for [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and libFuzzer. The targets are in [`FuzzTargets.cs`](AvroSharp.Fuzz/FuzzTargets.cs):

| Target | Input | Checks |
|---|---|---|
| `SchemaParse` | Schema JSON | Only `AvroException` escapes; a parsed schema writes and parses back to the same canonical form |
| `GenericBinary` | First byte picks a schema; the rest is binary data | Only `AvroException` escapes; an accepted value round-trips through binary (exactly) and JSON |
| `GenericJson` | First byte picks a schema; the rest is JSON data | Same as `GenericBinary` |
| `ContainerFile` | An object container file, read with a 1 MiB block limit | Only `AvroException` escapes; every object read round-trips like `GenericBinary` |
| `SingleObject` | A single-object encoded message of one of the schemas | Only `AvroException` escapes; the object round-trips |
| `Resolution` | First byte picks a writer/reader schema pair; the rest is writer data | The resolving reader and the transcoder that generated types use either both fail or give equal values |

The same targets run on every build as a seeded mutation smoke test (`tests/AvroSharp.Tests/Fuzz/FuzzSmokeTests.cs`). That catches crashes close to valid input but is not coverage-guided; use libFuzzer for longer runs.

## Running with libFuzzer

These steps follow the SharpFuzz documentation. They have not been run in this repository yet, so expect to adjust paths.

1. Install the instrumentation tool and download `libfuzzer-dotnet` for your platform from its [releases](https://github.com/Metalnem/libfuzzer-dotnet/releases):

   ```sh
   dotnet tool install --global SharpFuzz.CommandLine
   ```

2. Build the harness and instrument AvroSharp (only the library under test is instrumented):

   ```sh
   dotnet publish fuzz/AvroSharp.Fuzz -c Release -o out/fuzz
   sharpfuzz out/fuzz/AvroSharp.dll
   ```

3. Write the seed corpus, then run one target:

   ```sh
   out/fuzz/AvroSharp.Fuzz --write-seeds corpus
   libfuzzer-dotnet --target_path=out/fuzz/AvroSharp.Fuzz --target_arg=GenericBinary corpus/GenericBinary
   ```

A crash leaves its input in a `crash-*` file. To reproduce it, add the input to `FuzzSmokeTests` as a fixed case, fix the bug, and keep the case as a regression test.
