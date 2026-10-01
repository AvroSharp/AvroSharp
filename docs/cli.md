# The avrosharp command-line tool

`avrosharp` ([AvroSharp.Tool](https://www.nuget.org/packages/AvroSharp.Tool)) generates C# from Avro schema files, and prints schemas' canonical forms and fingerprints. It is a `dotnet tool`, like Apache.Avro's `avrogen`, and runs on .NET 8 or later.

On this page:
- [Install](#install)
- [gen: C# from schema files](#gen-c-from-schema-files)
- [schema canonical](#schema-canonical)
- [schema fingerprint](#schema-fingerprint)
- [Errors and exit codes](#errors-and-exit-codes)
- [In a build or CI](#in-a-build-or-ci)
- [Coming from avrogen](#coming-from-avrogen)

## Install

```shell
dotnet tool install --global AvroSharp.Tool
avrosharp --help
```

Or per repository, in a tool manifest, so everyone uses the same version:

```shell
dotnet new tool-manifest            # once per repository
dotnet tool install AvroSharp.Tool
dotnet tool run avrosharp --help    # or: dotnet avrosharp --help
```

With the .NET 10 SDK, `dnx` runs it once without installing it:

```shell
dnx AvroSharp.Tool gen schemas/ -o Generated/
```

Every command has `--help`: `avrosharp gen --help`, `avrosharp schema fingerprint --help`.

## gen: C# from schema files

```shell
avrosharp gen <inputs>... --output <folder> [options]
```

`gen` writes the same code as the [AvroSharp.Generators source generator](code-generation.md), which needs no tool at all. Use the tool when the generated code should be checked in, reviewed, or built by something other than MSBuild, or by an SDK older than .NET 10. The project that compiles the code references the [AvroSharp](https://www.nuget.org/packages/AvroSharp) package.

**Inputs** are `.avsc` files, or folders searched with their subfolders for `*.avsc` files. All of them are parsed together, so a schema may use named types that another file defines, in any order:

```shell
avrosharp gen schemas/ -o Generated/
avrosharp gen common.avsc orders/order.avsc orders/refund.avsc -o Generated/
```

**Output** is one `.g.cs` file per named type, in folders for its Avro namespace:

```text
Generated/com/example/events/Order.g.cs
Generated/com/example/events/Status.g.cs
```

With `--flat`, all files go into the output folder, named by full name (`Generated/com.example.events.Order.g.cs`). Other files in the output folder are left alone.

**Options**, which match the [generator's MSBuild properties](code-generation.md#msbuild-properties) and set the [`CodeGenOptions`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.html) of the same name:

| Option | Meaning |
|---|---|
| `-o`, `--output <folder>` | Where the files go (required). Created if it doesn't exist. |
| `--flat` | All files in the output folder, named by the type's full name, instead of namespace folders. |
| `-n`, `--namespace <name>` | The C# namespace for types that have no Avro namespace. Without it they go in the global namespace. |
| `-m`, `--namespace-map <avro:csharp>` | Another C# namespace for an Avro namespace and the namespaces under it, as avrogen's `--namespace a:b`. Repeatable; the longest match wins. Only the C# namespace changes, not the schema. Not with `--apache-compatible`. |
| `--logical-types native\|raw` | `native` (default): .NET types for logical types (`DateOnly`, `DateTimeOffset`, `Guid`, `decimal`…). `raw`: their underlying Avro types. |
| `--property-names pascal\|avro` | `pascal` (default): PascalCase properties. `avro`: the Avro field names as written, as avrogen does. Defaults to `avro` with `--apache-compatible`. |
| `--apache-compatible` | The types also work with Apache.Avro's `SpecificDatumWriter`/`Reader` and with code written for avrogen classes. The project then needs Apache.Avro. See the [compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode). |
| `--no-nullable` | No nullable reference type annotations, so the code compiles as C# 7.3 (netstandard2.0 and .NET Framework projects). |
| `--no-date-only` | For targets without `DateOnly` and `TimeOnly`: `date` becomes `DateTime`, and `time-*` `TimeSpan`. |
| `--language-version <n>` | The major C# version the code may use (7 or later, default 14). 7 means C# 7.2 or later, and implies `--no-nullable`. With 11 or later, .NET 8+ targets also get [`IAvroSerializable<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html). |

Examples:

```shell
# Avro namespaces com.acme and com.acme.* become Acme.Events and Acme.Events.*
avrosharp gen schemas/ -o Generated/ -m com.acme:Acme.Events

# For a netstandard2.0 library on C# 7.3
avrosharp gen schemas/ -o Generated/ --no-nullable --no-date-only --language-version 7

# Replacing avrogen output in an existing Apache.Avro project
avrosharp gen schemas/ -o Generated/ --apache-compatible
```

**Nothing is written if any schema is invalid**, so a failed run never leaves a half-updated folder. A property renamed to avoid a clash is reported as `info AVROGEN005`.

## schema canonical

Prints a schema's [Parsing Canonical Form](https://avro.apache.org/docs/1.12.0/specification/#parsing-canonical-form-for-schemas): the form that fingerprints are computed from, and that tells whether two schemas are the same for reading. It is the schema's [`CanonicalForm`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.CanonicalForm.html).

```shell
avrosharp schema canonical user.avsc
cat user.avsc | avrosharp schema canonical -
```

## schema fingerprint

Prints a fingerprint of the canonical form, as [`SchemaFingerprint`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.html) computes it.

```shell
avrosharp schema fingerprint user.avsc                                # CRC-64-AVRO, as hex bytes
avrosharp schema fingerprint user.avsc --format decimal               # as Java's SchemaNormalization.parsingFingerprint64 returns it
avrosharp schema fingerprint user.avsc --algorithm sha256 --format base64
```

| Option | Values |
|---|---|
| `-a`, `--algorithm` | `crc64` (default): the 64-bit CRC-64-AVRO (Rabin) fingerprint that single-object encoding and schema stores use. `md5`, `sha256`: those digests of the canonical form. |
| `-f`, `--format` | `hex` (default): the bytes as Avro writes them (CRC-64 little-endian, as in single-object encoding). `base64`: the same bytes. `decimal`: the CRC-64 as a signed 64-bit number, as Java prints it (with `crc64` only). |

**Both schema commands** take several files or folders, and then print one `path: result` line each. A schema that uses types from other files names those files with `-r`/`--reference`, which are parsed but not printed:

```shell
avrosharp schema fingerprint orders/ -r common.avsc
```

## Errors and exit codes

Errors are reported in the compiler's format, `path(line,column): error AVROGEN001: message`, so editors and CI logs link them to the schema.

| Exit code | Meaning |
|---|---|
| 0 | Success. |
| 1 | The command failed: an invalid schema, a missing file, or output that could not be written. |
| 2 | The command line is not valid: an unknown command or option, or a missing or invalid argument. |

Errors go to standard error; results and informational messages go to standard output.

## In a build or CI

To check in generated code and keep it in step with the schemas, regenerate in CI and fail when anything changed:

```shell
dotnet tool restore
dotnet avrosharp gen schemas/ -o src/Generated/
git diff --exit-code src/Generated/
```

To compare schemas across services, compare fingerprints:

```shell
test "$(avrosharp schema fingerprint a.avsc)" = "$(avrosharp schema fingerprint b.avsc)"
```

## Coming from avrogen

| avrogen | avrosharp |
|---|---|
| `avrogen -s schema.avsc out/` | `avrosharp gen schema.avsc -o out/` |
| `--namespace a:b` | `-m a:b` (or `--namespace-map a:b`) |
| classes for Apache.Avro | the same with `--apache-compatible`; without it, classes for AvroSharp |

[AvroSharp and Apache.Avro](apache-avro.md) covers moving a codebase over.
