# Contributing to AvroSharp

## Ground rules

- **Implement from the specification.** AvroSharp is a clean-room implementation of the Apache Avro™ specification under the MIT license. Apache.Avro (Apache-2.0) may be read for design ideas and used as a test oracle, but its code must not be copied or ported into `src/`. Code derived from Chr.Avro (MIT) must keep its copyright notice in `THIRD-PARTY-NOTICES.md`.
- **No reflection on serialization paths.** Typed serialization is produced by source generators and must stay Native AOT and trimming compatible.
- **Performance is a feature.** AvroSharp must be faster than Apache.Avro, with fewer allocations, on every scenario in the benchmark suite. Benchmarks run locally only, on request, on an idle machine (never in CI):

  ```shell
  dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*' --runtimes net8.0 net9.0 net10.0 --memory --gate
  ```
- **Codecs use fully managed libraries only.** No native binaries and no P/Invoke.
- **Tests must exercise the product path.** Write the test, revert the fix, and confirm the test fails.

## Building and testing

```shell
dotnet build
dotnet test --solution AvroSharp.slnx
dotnet format --verify-no-changes
```

On Windows the test project also targets `net481`, which runs the `netstandard2.0` build on .NET Framework.

Native AOT smoke test:

```shell
dotnet publish tests/AvroSharp.AotSmoke -c Release -r win-x64   # or linux-x64
```

## Public API

Public API is tracked with `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Add new members to `PublicAPI.Unshipped.txt`; the build fails otherwise.

## Workflow

Branch, open a pull request, and merge (squash) once CI is green. `main` is never pushed to directly.
