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
dotnet build -c Release
dotnet test --solution AvroSharp.slnx -c Release -f net10.0
```

Locally, net10.0 is enough while working. Before opening or updating a pull request, also run `-f net481`, which tests the `netstandard2.0` build on .NET Framework: CI currently runs on Linux only (net8.0, net9.0, net10.0 on x64 and Arm64), and its Windows runners are disabled until packages are published.

Formatting is not checked in CI. To check or fix it locally:

```shell
dotnet format AvroSharp.slnx --verify-no-changes   # check
dotnet format AvroSharp.slnx                       # fix
```

Native AOT smoke test:

```shell
dotnet publish tests/AvroSharp.AotSmoke -c Release -r win-x64   # or linux-x64
```

## Public API

Public API is tracked with `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Add new members to `PublicAPI.Unshipped.txt`; the build fails otherwise.

## Workflow

Branch, open a pull request, and merge (squash) once CI is green. `main` is never pushed to directly.

## Releasing

Versions come from git tags through MinVer: `v1.2.3`, or `v1.2.3-alpha.1` for a pre-release.

1. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [1.2.3] - YYYY-MM-DD` (the exact version, pre-release suffix included) and start a new empty `[Unreleased]` section. Merge that.
2. Rehearse: run the **Release** workflow manually on `main` with `publish` off. It tests on Linux and Windows (including net481), packs, and checks the release notes, but pushes nothing.
3. Tag the merge commit and push the tag: `git tag v1.2.3 && git push origin v1.2.3`. The workflow tests again, packs, pushes the packages and symbols to nuget.org, and creates the GitHub release from the CHANGELOG section, marked as a pre-release when the version has a suffix. It fails if the tag and the packed version differ, or if the CHANGELOG has no section for the version.

The push needs a `NUGET_API_KEY` secret in the repository's `nuget` environment. It should be a nuget.org key that can push `AvroSharp*` packages only (see #84).
