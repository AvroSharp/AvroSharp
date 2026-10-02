# <img src="docs/images/logo.png" alt="AvroSharp">

A high-performance, Native AOT-friendly .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification: schemas, binary and JSON encoding, schema evolution, source-generated serializers, container files with every codec, single-object encoding and schema-registry framing.

[Get started](README.md#getting-started) · [Code generation](docs/code-generation.md) · [Command-line tool](docs/cli.md) · [Kafka and Confluent](docs/confluent.md) · [Integrations](docs/integrations.md) · [API reference](docs/api/index.md) · [Benchmarks](docs/benchmarks.md) · [Compared with Apache.Avro](docs/apache-avro.md) · [GitHub](https://github.com/AvroSharp/AvroSharp)

> **Status:** a release candidate for 1.0.0. The public API is frozen, and from 1.0 it follows [semantic versioning](https://semver.org/): no breaking changes before 2.0.

## Why AvroSharp

- **Fast:** faster than Apache.Avro on every benchmark, and never allocates more: generated records read 3.95× and write 6.18× faster, and container files read up to 22× faster. [Benchmarks](docs/benchmarks.md)
- **Generated at build time:** a source generator turns `.avsc` files into C# types with serializers, with no reflection, so they work with Native AOT and trimming. [Code generation](docs/code-generation.md)
- **Complete:** Avro 1.12 schemas, binary and JSON encoding, schema resolution, container files with every codec, single-object encoding, canonical forms and fingerprints, and every logical type. [Guide](README.md)
- **Interoperable:** the same bytes as Apache.Avro and Apache Avro Java, checked by tests in both directions, with the specification followed where Apache.Avro deviates. [Compared with Apache.Avro](docs/apache-avro.md)
- **Safe with untrusted data:** bounded memory and nesting for hostile files, messages and schemas, and fuzzed every night. [Hostile input](docs/apache-avro.md#hostile-input)
- **Plugs into Kafka:** AvroSharp.Confluent gives Confluent.Kafka serializers with Confluent Schema Registry: the same bytes and settings as Confluent's Avro serializer, without Apache.Avro. AvroSharp.KafkaFlow does the same for KafkaFlow, AvroSharp.Azure.SchemaRegistry for Event Hubs and Service Bus with Azure Schema Registry, and AvroSharp.Aws.Glue for AWS Glue Schema Registry. [Integrations](docs/integrations.md)

## Packages

| Package | What it is | Docs |
|---|---|---|
| [AvroSharp](https://www.nuget.org/packages/AvroSharp) | The runtime: schemas ([`AvroSharp.Schemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.html)), readers and writers ([`AvroSharp.IO`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.html), [`AvroSharp.Serialization`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.html)), the generic model ([`AvroSharp.Generic`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html)), container files ([`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html)), messages ([`AvroSharp.Messages`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.html)) and streams ([`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html)) | [Guide](README.md), [API](docs/api/index.md) |
| [AvroSharp.Generators](https://www.nuget.org/packages/AvroSharp.Generators) | The source generator: C# types from `.avsc` files as the project builds | [Code generation](docs/code-generation.md) |
| [AvroSharp.Tool](https://www.nuget.org/packages/AvroSharp.Tool) | `avrosharp`, the `dotnet tool`: code generation, canonical forms and fingerprints from the command line | [Command-line tool](docs/cli.md) |
| [AvroSharp.Codecs](https://www.nuget.org/packages/AvroSharp.Codecs) | The snappy, zstandard, bzip2 and xz codecs, fully managed | [Codecs](README.md#object-container-files), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html) |
| [AvroSharp.CodeGen](https://www.nuget.org/packages/AvroSharp.CodeGen) | The code generation engine, for your own tools: [`CSharpCodeGenerator`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CSharpCodeGenerator.html) | [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html) |

### Integrations

Add-on packages, built from the same repository and released with AvroSharp at the same version.

| Package | For | Docs |
|---|---|---|
| [AvroSharp.Confluent](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Confluent) | Kafka with Confluent Schema Registry: serializers for Confluent.Kafka, the same bytes and settings as Confluent's Avro serializer, without Apache.Avro. New in the release candidates. | [Guide](docs/confluent.md), [sample](samples/Confluent/Program.cs), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Confluent.html) |
| [AvroSharp.KafkaFlow](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.KafkaFlow) | KafkaFlow producers and consumers, on AvroSharp.Confluent: the same bytes as KafkaFlow's Confluent Avro serializer, and several record types per topic. New in the release candidates. | [Guide](docs/kafkaflow.md), [sample](samples/KafkaFlowEvents/Program.cs), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.KafkaFlow.html) |
| [AvroSharp.Azure.SchemaRegistry](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Azure.SchemaRegistry) | Azure Schema Registry with Event Hubs and Service Bus: the message format of Microsoft's Avro serializer, without Apache.Avro. New in the release candidates. | [Guide](docs/azure-schema-registry.md), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Azure.SchemaRegistry.html) |
| [AvroSharp.Aws.Glue](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Aws.Glue) | AWS Glue Schema Registry, fully managed, on every platform. New in the release candidates. | [Guide](docs/aws-glue.md), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.html) |
| [AvroSharp.Aws.Glue.Kafka](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Aws.Glue.Kafka) | Confluent.Kafka serializers for AWS Glue Schema Registry, on AvroSharp.Aws.Glue. New in the release candidates. | [Guide](docs/aws-glue.md), [API](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.Kafka.html) |

## Guides

- [Getting started](docs/getting-started/index.md): installing, a first program, and a page per task, each built from a runnable sample.
- [First steps](README.md#getting-started): parse a schema, write and read values, read an older version of the data, JSON ([`AvroSharp.Schemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.html), [`AvroSharp.Generic`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html)).
- [Object container files](README.md#object-container-files): writing and reading `.avro` files, synchronously and asynchronously, with any codec ([`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html), [`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html)).
- [Single-object encoding](README.md#single-object-encoding) and [schema registries](README.md#schema-registries): messages that carry their schema's fingerprint or ID, with Confluent, Apicurio and AWS Glue framing ([`AvroSharp.Messages`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.html)).
- [Streams of objects](README.md#streams-of-objects): objects one after another, for sockets and pipes ([`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html)).
- [Integrations](docs/integrations.md): message brokers and schema registries, AvroSharp.Confluent for Confluent.Kafka, AvroSharp.KafkaFlow for KafkaFlow, AvroSharp.Azure.SchemaRegistry for Azure Schema Registry, and AvroSharp.Aws.Glue for AWS Glue Schema Registry.
- [Kafka and Confluent Schema Registry](docs/confluent.md): AvroSharp.Confluent's serializers for Confluent.Kafka, their settings, and moving from Confluent's Avro serializer and from Chr.Avro.
- [KafkaFlow](docs/kafkaflow.md): AvroSharp.KafkaFlow's serializer middleware, several record types per topic, and moving from KafkaFlow's Confluent Avro serializer.
- [Azure Schema Registry](docs/azure-schema-registry.md): AvroSharp.Azure.SchemaRegistry's serializer for Event Hubs and Service Bus, and moving from Microsoft's Avro serializer.
- [AWS Glue Schema Registry](docs/aws-glue.md): AvroSharp.Aws.Glue's managed serializer for Kafka and Kinesis, and moving from AWS's native serializer.
- [Code generation](docs/code-generation.md): the source generator, its MSBuild properties, type mapping, schema evolution, and moving from avrogen.
- [The avrosharp tool](docs/cli.md): `gen`, `schema canonical` and `schema fingerprint`, exit codes, and use in CI.
- [Samples](samples/README.md): runnable programs for the main APIs.

## Reference

- [API reference](docs/api/index.md): every public type, from the XML documentation, by namespace: [`AvroSharp`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.html), [`AvroSharp.Schemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.html), [`AvroSharp.Generic`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html), [`AvroSharp.IO`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.html), [`AvroSharp.Serialization`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.html), [`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html), [`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html), [`AvroSharp.Messages`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.html), [`AvroSharp.Streams`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.html), [`AvroSharp.CodeGen`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html), [`AvroSharp.Confluent`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Confluent.html), [`AvroSharp.KafkaFlow`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.KafkaFlow.html), [`AvroSharp.Azure.SchemaRegistry`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Azure.SchemaRegistry.html), [`AvroSharp.Aws.Glue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.html) and [`AvroSharp.Aws.Glue.Kafka`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Aws.Glue.Kafka.html).
- [Benchmarks](docs/benchmarks.md): the results against Apache.Avro, what is measured, and how to run it.
- [AvroSharp and Apache.Avro](docs/apache-avro.md): what differs, when to use which, and how to migrate.
- [Design](docs/design.md): the design and the decisions made since.
- [Roadmap](docs/roadmap.md): what is done and what comes next.
- [Documentation index](docs/README.md): all pages, and the dated benchmark and review notes.
- [Changelog](CHANGELOG.md), [contributing](CONTRIBUTING.md), [security policy](SECURITY.md).

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
