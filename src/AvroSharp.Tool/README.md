# AvroSharp.Tool

`avrosharp`, the command-line tool of [AvroSharp](https://github.com/AvroSharp/AvroSharp): C# types and serializers from Apache Avro™ schema files (`.avsc`), the canonical form and fingerprints of schemas, schema compatibility checks, and avro-tools' commands for container files (`file tojson`, `fromjson`, `getschema`, `getmeta`, `count`, `cat`, `concat`, `recodec`, `random`, `repair`). It is a `dotnet tool`, like Apache.Avro's `avrogen`, and runs on .NET 8 or later.

> **Status:** stable. It follows [semantic versioning](https://semver.org/).

**[Command-line tool documentation](https://avrosharp.github.io/AvroSharp/docs/cli.html)**: every command and option, examples, and use in CI. Also: [the source generator](https://avrosharp.github.io/AvroSharp/docs/code-generation.html), [documentation](https://avrosharp.github.io/AvroSharp/).

```shell
dotnet tool install --global AvroSharp.Tool     # or without --global, in a tool manifest
avrosharp --help
```

With the .NET 10 SDK, `dnx AvroSharp.Tool gen ...` runs it once without installing it.

## gen: C# from schema files

```shell
avrosharp gen schemas/ --output Generated/ --namespace Acme.Events
```

It writes one `.g.cs` file per named type, in folders for its namespace (`Generated/com/example/events/Order.g.cs`), or with `--flat` all in the output folder, named by full name (`Generated/com.example.events.Order.g.cs`). It is the same code as the [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) source generator, from the same engine ([`AvroSharp.CodeGen`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html)), and the generator needs no tool at all. Use the tool when the code should be checked in, or built by something other than MSBuild. The project that compiles the code references the [AvroSharp](https://www.nuget.org/packages/AvroSharp) package.

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
  | `--required-members` | Fields without a schema default become `required` members (.NET 7+, C# 11+). |
  | `--type-kind class\|record` | Classes (default), or C# record classes with value equality. |
  | `--accessibility public\|internal` | Public (default) or internal types. |
  | `--unsealed` | Records and fixed types are not sealed. |
  | `--collection-type list\|array\|readonlylist` | Avro arrays as `List<T>` (default), `T[]` or `IReadOnlyList<T>`. |
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

The results are those of [`AvroSchema.CanonicalForm`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.CanonicalForm.html) and [`SchemaFingerprint`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.html) in the library. Several files or folders give one `path: result` line each. Schemas that use types from other files name them with `--reference` (`-r`).

## schema compat: can one schema read another's data

```shell
avrosharp schema compat v1.avsc v2.avsc                              # the writer's schema, then the reader's
avrosharp schema compat --level backward-transitive v1.avsc v2.avsc v3.avsc   # the last against the earlier ones
avrosharp schema compat v1.avsc v2.avsc --json --warnings-as-errors
```

It prints the verdict and every issue, each with its path into the data. The check is [`AvroSchemaCompatibility`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaCompatibility.html) in the library.
- `--level` takes Confluent Schema Registry's levels: `backward`, `forward` and `full`, each with a `-transitive` version.
- `--warnings-as-errors` (or `--strict`) fails on warnings, such as a changed decimal scale.
- `--allow-partial` passes when only some values can't be read.

## file: container files

```shell
avrosharp file tojson events.avro                                    # one JSON value per line, as avro-tools prints them
avrosharp file fromjson events.json --schema event.avsc -o events.avro
avrosharp file concat 2026-11-*.avro -o november.avro                # block by block, without decoding
avrosharp file recodec events.avro --codec zstandard -o events.zst.avro
avrosharp file random --schema event.avsc --count 1000 --seed 1 -o sample.avro
avrosharp file repair damaged.avro -o repaired.avro
```

Also `getschema`, `getmeta`, `count` and `cat` (with `--offset` and `--limit`). Every codec of the specification is read and written; `-` is standard input, and `-o -` standard output.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | The command failed: an invalid schema, a missing file, or output that could not be written. |
| 2 | The command line is not valid: an unknown command or option, a missing or invalid argument. |
| 3 | `schema compat`: partially compatible; some values can't be read. |
| 4 | `schema compat`: incompatible, or warnings with `--warnings-as-errors`. |

Errors go to standard error, results and informational messages to standard output.
