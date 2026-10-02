# Contributing to AvroSharp

## Ground rules

- **Implement from the specification.** AvroSharp is a clean-room implementation of the Apache Avro™ specification under the MIT license. Apache.Avro (Apache-2.0) may be read for design ideas and used as a test oracle, but its code must not be copied or ported into `src/`. Code derived from Chr.Avro (MIT) must keep its copyright notice in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
- **No reflection on serialization paths.** Typed serialization is produced by source generators and must stay Native AOT and trimming compatible.
- **Performance is a feature.** AvroSharp must be faster than Apache.Avro, with fewer allocations, on every scenario in the [benchmark suite](docs/benchmarks.md). Benchmarks run locally only, on request, on an idle machine (never in CI):

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

Locally, net10.0 is enough while working. CI runs net8.0, net9.0 and net10.0 on Linux and Windows, x64 and Arm64, and on Windows also net481, which tests the `netstandard2.0` builds on .NET Framework (the add-ons too, except AvroSharp.KafkaFlow, which has no .NET Framework build; their test projects register the generated types first, as a .NET Framework application does). Run `-f net481` locally too when a change touches the netstandard code paths.

AvroSharp.Confluent's, AvroSharp.KafkaFlow's and AvroSharp.Aws.Glue's tests need more:
- **Redpanda:** the tests against a real broker and registry need Docker with Linux containers. They are `[Explicit]`, so they run only when a filter names them: `dotnet test --project tests/AvroSharp.Confluent.Tests --treenode-filter "/*/*/RedpandaTests/*"`, and AvroSharp.KafkaFlow's with `--project tests/AvroSharp.KafkaFlow.Tests --treenode-filter "/*/*/KafkaFlowRedpandaTests/*"`. Otherwise they are neither run nor listed.
- **moto:** AvroSharp.Aws.Glue's tests against moto, an AWS emulator in Docker, are `[Explicit]` too: `dotnet test --project tests/AvroSharp.Aws.Glue.Tests --treenode-filter "/*/*/MotoGlueTests/*"`.
- **Confluent's lowest supported version:** the package depends on Confluent.SchemaRegistry [2.14.0, 3.0.0). The build uses the newest by default; to test against 2.14.0, build and test with `-p:ConfluentVersion=2.14.0` (and an `-p:ArtifactsPath` of its own, to keep the main build). CI does this on the x64 Linux runner.
- **The other add-ons' lowest versions:** likewise `-p:KafkaFlowVersion=4.0.0` (with `ConfluentVersion=2.14.0`), `-p:AzureSchemaRegistryVersion=1.2.0` and `-p:AwsGlueVersion=4.0.0`. CI runs all of them in one step.

CI runs them with the rest of the solution, and the Redpanda and moto tests in a step of their own on the x64 Linux runner. The Confluent and KafkaFlowEvents samples need a broker and a registry too: `docker compose up -d --wait` in `samples/Confluent` starts Redpanda for both.

Formatting is not checked in CI. To check or fix it locally:

```shell
dotnet format AvroSharp.slnx --verify-no-changes   # check
dotnet format AvroSharp.slnx                       # fix
```

Native AOT smoke test:

```shell
dotnet publish tests/AvroSharp.AotSmoke -c Release -r win-x64          # or linux-x64
dotnet publish tests/AvroSharp.AotSmoke.Addons -c Release -r win-x64   # the add-on packages
```

The documentation site, with the same DocFX version and broken-link check as the docs workflow:

```shell
dotnet tool restore
dotnet tool run docfx docfx.json --serve   # http://localhost:8080
```

### Dev container

`.devcontainer/` builds and tests as the Linux CI job does, with no local .NET setup. It has:
- **SDKs:** Ubuntu 24.04 (as `ubuntu-latest`) with the .NET 8, 9 and 10 SDKs;
- **Native AOT:** its prerequisites;
- **Tools:** the repository's local tools (DocFX, ReportGenerator);
- **Environment:** CI's variables;
- **Cache:** the NuGet cache in a volume, so rebuilding the container doesn't download it again.

Open the repository in it with VS Code (**Dev Containers: Reopen in Container**) or Rider, or create a GitHub Codespace from the repository page (**Code > Codespaces**). On Windows, use **Dev Containers: Clone Repository in Container Volume**, or a clone inside WSL. A clone on the Windows file system is slow when bind-mounted, and its permissions can break the build. Then run the Linux CI job's steps:

```shell
build/ci-local.sh
```

It runs, in CI's order:
- restore and build;
- the tests on net8.0, net9.0 and net10.0 with coverage;
- the Redpanda and moto tests, when Docker is available (the dev container has no Docker inside it, so there they are left out);
- the add-ons' tests against the lowest versions of their dependencies (Confluent 2.14.0, KafkaFlow 4.0.0, Azure.Data.SchemaRegistry 1.2.0, AWSSDK.Glue 4.0.0);
- the tests without hardware intrinsics;
- the coverage check (the summary is in `artifacts/coverage/SummaryGithub.md`);
- the samples;
- the Native AOT smoke tests, of the core packages and of the add-ons;
- pack, and the package consumers.

Behind a proxy that intercepts HTTPS, the image build and restores fail with certificate errors, because the container doesn't trust the proxy's root certificate the way the host does. Add the certificate in a local copy of the Dockerfile, and don't commit it: `COPY proxy-root.crt /usr/local/share/ca-certificates/` and then `RUN update-ca-certificates`, right after `FROM`.

It takes about as long as the CI job. Passing it means passing the Linux CI job on the container's architecture: `ubuntu-latest` on x64, or `ubuntu-24.04-arm` on an Arm64 host such as an Apple silicon Mac.

It doesn't cover:
- **Windows and .NET Framework:** the Windows jobs and the net481 tests run in CI only.
- **The other architecture:** the container runs on the host's architecture, so only CI runs both.
- **Fuzzing:** the nightly libFuzzer runs are in [`fuzz/README.md`](fuzz/README.md). The random-schema test runs with the other tests, on 100 schemas.
- **Benchmarks:** these need a quiet, dedicated machine, not a container.

## Public API

The core packages' public API is frozen for 1.0, and from 1.0 follows [semantic versioning](https://semver.org/): a breaking change waits for the next major version. The add-on packages (AvroSharp.Confluent, AvroSharp.KafkaFlow, AvroSharp.Azure.SchemaRegistry, AvroSharp.Aws.Glue and AvroSharp.Aws.Glue.Kafka) are new in the release candidates, and their APIs may still change until 1.0.0. The [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html) shows it.

Public API is tracked with `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Add new members to `PublicAPI.Unshipped.txt` (members that exist only on .NET 8 and later go in `src/AvroSharp/PublicAPI/net8.0/`); the build fails otherwise. Package validation also compares each package with its baseline release (`PackageValidationBaselineVersion` in `src/Directory.Build.props`), and a break that isn't listed in the project's `CompatibilitySuppressions.xml` fails the pack. The add-on packages have no baseline yet (an empty `PackageValidationBaselineVersion`); they get one with 1.0.0.

## Workflow

Branch, open a pull request, and merge (squash) once CI is green. `main` is never pushed to directly.

## Releasing

Versions come from git tags through MinVer: `v1.2.3`, or `v1.2.3-alpha.1` for a pre-release.

1. Prepare a release pull request, and merge it:
   - in `CHANGELOG.md`, rename `## [Unreleased]` to `## [1.2.3] - YYYY-MM-DD` (the exact version, pre-release suffix included), start a new empty `[Unreleased]` section, and update the links at the bottom: `[Unreleased]` compares the new tag with `HEAD`, and the new version links to its release;
   - move the API listings to Shipped: the lines of each `PublicAPI.Unshipped.txt` (and `src/AvroSharp/PublicAPI/net8.0/`) go into the `PublicAPI.Shipped.txt` beside it, and the rules in `AnalyzerReleases.Unshipped.md` go into `AnalyzerReleases.Shipped.md` under `## Release 1.2.3`, so a later change to shipped API is reported;
   - check that the package READMEs' status lines and `docs/roadmap.md` still describe the release: package READMEs are packed into the immutable `.nupkg`.
2. Rehearse: run the **Release** workflow manually on `main` with `publish` off. It tests on Linux and Windows (including net481), packs, and checks the release notes, but pushes nothing.
3. Tag the merge commit and push the tag: `git tag v1.2.3 && git push origin v1.2.3`. The workflow tests again, packs, pushes the packages and symbols to nuget.org, and creates the GitHub release from the CHANGELOG section, marked as a pre-release when the version has a suffix. It fails if the tag and the packed version differ, or if the CHANGELOG has no section for the version.

The push uses nuget.org's [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored. It needs, once:

- **on nuget.org:** a trusted publishing policy for the nuget.org account that owns the `AvroSharp*` packages (`zcsizmadia`). The policy names repository owner `AvroSharp` (the organization; the repository moved there from `zcsizmadia` on 2026-09-30), repository `AvroSharp`, workflow file `release.yml` and environment `nuget`.
- **on GitHub:** an environment named `nuget` (Settings → Environments; add required reviewers there to approve each publish), and a repository variable `NUGET_USER` holding that nuget.org account or organization name.

The workflow asks GitHub for an OIDC token, and `NuGet/login` exchanges it for a key that is valid for about an hour.
