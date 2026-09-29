# AvroSharp.Tool

`avrosharp`, the command-line tool of [AvroSharp](https://github.com/zcsizmadia/AvroSharp): C# types and serializers from Apache Avro™ schema files (`.avsc`), and the canonical form and fingerprints of schemas. It is a `dotnet tool`, like Apache.Avro's `avrogen`, and runs on .NET 8 or later.

> **Status:** an early preview (0.x). The API may still change before 1.0.

**[Command-line tool documentation](https://zcsizmadia.github.io/AvroSharp/docs/cli.html)**: every command and option, examples, and use in CI. Also: [the source generator](https://zcsizmadia.github.io/AvroSharp/docs/code-generation.html), [documentation](https://zcsizmadia.github.io/AvroSharp/).

```shell
dotnet tool install --global AvroSharp.Tool     # or without --global, in a tool manifest
avrosharp --help
```

With the .NET 10 SDK, `dnx AvroSharp.Tool gen ...` runs it once without installing it.

## gen: C# from schema files

```shell
avrosharp gen schemas/ --output Generated/ --namespace Acme.Events
```

It writes one `.g.cs` file per named type, in folders for its namespace (`Generated/com/example/events/Order.g.cs`), or with `--flat` all in the output folder, named by full name (`Generated/com.example.events.Order.g.cs`). It is the same code as the [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) source generator, which needs no tool at all. Use the tool when the code should be checked in, or built by something other than MSBuild. The project that compiles the code references the [AvroSharp](https://www.nuget.org/packages/AvroSharp) package.

- **Inputs** are files, or folders searched with their subfolders for `*.avsc` files. They may refer to named types that other files define, in any order.
- **Options**, as the generator's MSBuild settings:

  | Option | Meaning |
  |---|---|
  | `-o`, `--output <folder>` | Where the files go (required). Other files in it are left alone. |
  | `--flat` | All files in the output folder, named by the type's full name, instead of in namespace folders. |
  | `-n`, `--namespace <name>` | The C# namespace for types that have no Avro namespace. |
  | `-m`, `--namespace-map <avro:csharp>` | Another C# namespace for an Avro namespace and the ones under it, as avrogen's `--namespace a:b`. Repeatable; the longest match wins. The schema is unchanged. Not with `--apache-compatible`. |
  | `--logical-types native\|raw` | .NET types for logical types (default), or their underlying Avro types. |
  | `--property-names pascal\|avro` | PascalCase properties (default), or the Avro field names as avrogen uses them. |
  | `--apache-compatible` | The types also work with Apache.Avro's `SpecificDatumWriter`/`Reader`. |
  | `--no-nullable` | No nullable annotations: C# 7.3, for netstandard2.0 and .NET Framework projects. |
  | `--no-date-only` | For targets without `DateOnly`/`TimeOnly`: `DateTime` and `TimeSpan` instead. |
  | `--language-version <n>` | The C# version the code may use (default 14). |

- **Nothing is written if any schema is invalid.** Each problem is reported as `path(line,column): error AVROGEN001: message`, the compiler's format.

## schema: canonical form and fingerprints

```shell
avrosharp schema canonical user.avsc
avrosharp schema fingerprint user.avsc                              # CRC-64-AVRO, as hex bytes
avrosharp schema fingerprint user.avsc --algorithm sha256 --format base64
avrosharp schema fingerprint user.avsc --format decimal             # as Java's parsingFingerprint64 returns it
cat user.avsc | avrosharp schema canonical -
```

Several files or folders give one `path: result` line each. Schemas that use types from other files name them with `--reference` (`-r`).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | The command failed: an invalid schema, a missing file, or output that could not be written. |
| 2 | The command line is not valid: an unknown command or option, a missing or invalid argument. |

Errors go to standard error, results and informational messages to standard output.
