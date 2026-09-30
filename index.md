# AvroSharp

A high-performance, Native AOT-friendly .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification: schemas, binary and JSON encoding, schema evolution, source-generated serializers, container files with every codec, single-object encoding and schema-registry framing.

[Get started](README.md#getting-started) · [Code generation](docs/code-generation.md) · [Command-line tool](docs/cli.md) · [API reference](docs/api/index.md) · [Benchmarks](docs/benchmarks.md) · [Compared with Apache.Avro](docs/apache-avro.md) · [GitHub](https://github.com/zcsizmadia/AvroSharp)

> **Status:** an early preview (0.x). The API may still change before 1.0.

## Why AvroSharp

- **Fast:** faster than Apache.Avro on every benchmark, and never allocates more: generated records read 3.95× and write 6.18× faster, and container files read up to 22× faster. [Benchmarks](docs/benchmarks.md)
- **Generated at build time:** a source generator turns `.avsc` files into C# types with serializers, with no reflection, so they work with Native AOT and trimming. [Code generation](docs/code-generation.md)
- **Complete:** Avro 1.12 schemas, binary and JSON encoding, schema resolution, container files with every codec, single-object encoding, canonical forms and fingerprints, and every logical type. [Guide](README.md)
- **Interoperable:** the same bytes as Apache.Avro and Apache Avro Java, checked by tests in both directions, with the specification followed where Apache.Avro deviates. [Compared with Apache.Avro](docs/apache-avro.md)
- **Safe with untrusted data:** bounded memory and nesting for hostile files, messages and schemas, and fuzzed every night. [Hostile input](docs/apache-avro.md#hostile-input)

## Packages

| Package | What it is | Docs |
|---|---|---|
| [AvroSharp](https://www.nuget.org/packages/AvroSharp) | The runtime: schemas, readers and writers, the generic model, container files, messages and streams | [Guide](README.md), [API](docs/api/index.md) |
| [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) | The source generator: C# types from `.avsc` files as the project builds | [Code generation](docs/code-generation.md) |
| [AvroSharp.Tool](https://www.nuget.org/packages/AvroSharp.Tool) | `avrosharp`, the `dotnet tool`: code generation, canonical forms and fingerprints from the command line | [Command-line tool](docs/cli.md) |
| [AvroSharp.Codecs](https://www.nuget.org/packages/AvroSharp.Codecs) | The snappy, zstandard, bzip2 and xz codecs, fully managed | [Codecs](README.md#object-container-files) |
| [AvroSharp.CodeGen](https://www.nuget.org/packages/AvroSharp.CodeGen) | The code generation engine, for your own tools | [API](docs/api/index.md) |

## Guides

- [Getting started](README.md#getting-started): parse a schema, write and read values, read an older version of the data, JSON.
- [Object container files](README.md#object-container-files): writing and reading `.avro` files, synchronously and asynchronously, with any codec.
- [Single-object encoding](README.md#single-object-encoding) and [schema registries](README.md#schema-registries): messages that carry their schema's fingerprint or ID, with Confluent, Apicurio and AWS Glue framing.
- [Streams of objects](README.md#streams-of-objects): objects one after another, for sockets and pipes.
- [Integrations](docs/integrations.md): message brokers and schema registries, and the planned add-on packages for Confluent.Kafka, KafkaFlow, Azure Schema Registry and AWS Glue.
- [Code generation](docs/code-generation.md): the source generator, its MSBuild properties, type mapping, schema evolution, and moving from avrogen.
- [The avrosharp tool](docs/cli.md): `gen`, `schema canonical` and `schema fingerprint`, exit codes, and use in CI.
- [Samples](samples/README.md): runnable programs for the main APIs.

## Reference

- [API reference](docs/api/index.md): every public type, from the XML documentation.
- [Benchmarks](docs/benchmarks.md): the results against Apache.Avro, what is measured, and how to run it.
- [AvroSharp and Apache.Avro](docs/apache-avro.md): what differs, when to use which, and how to migrate.
- [Design](docs/design.md): the design and the decisions made since.
- [Roadmap](docs/roadmap.md): what is done and what comes next.
- [Documentation index](docs/README.md): all pages, and the dated benchmark and review notes.
- [Changelog](CHANGELOG.md), [contributing](CONTRIBUTING.md), [security policy](SECURITY.md).

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
