# Integrations

AvroSharp works with message brokers and schema registries at two levels. The **AvroSharp package itself** writes and reads each registry's wire framing, with no dependency on a registry client. **Add-on packages** plug AvroSharp into the clients and frameworks applications already use, starting with Confluent.Kafka ([`AvroSharp.Confluent`](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Confluent)). They're tracked in [#77](https://github.com/AvroSharp/AvroSharp/issues/77), and [feature requests](https://github.com/AvroSharp/AvroSharp/issues) for others are welcome.

## In AvroSharp today

- **Registry wire framing** for Confluent (4-byte IDs, and GUIDs with the `__value_schema_id` header), Apicurio (4- and 8-byte IDs) and AWS Glue (with or without zlib): [`AvroRegistryFraming`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryFraming.html), [`AvroRegistryMessage`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessage.html) and [`AvroRegistryMessageReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessageReader.html), described in [schema registries](../README.md#schema-registries). Confluent's framing is byte-identical to Confluent's own serializer.
- **Schema references** across subjects ([`AvroSchemaParser.AddNamedSchemas`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaParser.AddNamedSchemas.html)), and schema JSON ([`AvroSchema.ToJson`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.ToJson.html)) that is Java's `Schema.toString()` byte for byte, so registering AvroSharp's text finds the version a Java client registered.
- **Field transforms** over the generic model ([`AvroValueTransformer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValueTransformer.html)), with each field's path and properties: the building block for data-quality rules, field-level encryption and masking.
- **Container files** for anything that writes `.avro` files, such as Azure Event Hubs Capture: [`AvroFileReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.html), with every codec in [AvroSharp.Codecs](../README.md#object-container-files).
- **Serializers by type** for frameworks that are handed a `T` or a `Type`, such as a Kafka serializer or AWS Lambda Powertools' `Deserialize(byte[], Type)`: [`AvroTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroTypes.html) gives the schema and the read and write functions of every generated type, `[AvroSerializable]` ones included, and of the primitives `string`, `int`, `long`, `float`, `double`, `bool` and `byte[]`, without reflection and on every target.

With Confluent.Kafka, use `AvroSharp.Confluent` below. Without it, an application registers its schema with Confluent's registry client, and produces and consumes `byte[]` values framed by `AvroRegistryMessage` and read by `AvroRegistryMessageReader` with a resolver ([`IAvroSchemaIdResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaIdResolver.html)) that fetches schemas by ID.

## Add-on packages

| Package | For | Status |
|---|---|---|
| `AvroSharp.Confluent` | Confluent.Kafka with Confluent Schema Registry, and registries with its API (Redpanda, Karapace, Apicurio) | In the 1.0.0 release candidates: [its README](https://github.com/AvroSharp/AvroSharp/tree/main/src/AvroSharp.Confluent), [#185](https://github.com/AvroSharp/AvroSharp/issues/185) |
| `AvroSharp.KafkaFlow` | KafkaFlow producers and consumers, on `AvroSharp.Confluent` | Planned: [#154](https://github.com/AvroSharp/AvroSharp/issues/154) |
| `AvroSharp.Azure.SchemaRegistry` | Azure Schema Registry with Event Hubs and Service Bus (`MessageContent`) | Planned: [#155](https://github.com/AvroSharp/AvroSharp/issues/155) |
| `AvroSharp.Aws.Glue` | AWS Glue Schema Registry, fully managed, on every platform | Planned: [#156](https://github.com/AvroSharp/AvroSharp/issues/156) |

**`AvroSharp.Confluent`** is built on Confluent's own serializer base classes, so subject name strategies, auto-registration, `use.latest.version`, schema ID strategies and rules behave as they do with Confluent's serializer. It depends on `Confluent.SchemaRegistry` only, not on Apache.Avro. Its messages are byte-identical to Confluent's Avro serializer's, and both read each other's. The prototype serialized 8.7× and deserialized 6.5× faster, allocating 39% and 23% as much; the package's benchmarks are [#197](https://github.com/AvroSharp/AvroSharp/issues/197).

Frameworks that wrap Confluent's serializers, such as Streamiz, Silverback and MassTransit, will be covered by `AvroSharp.Confluent` and documentation rather than packages of their own. Apache Iceberg manifests are planned after 1.0. Pulsar, AWS Lambda Powertools and CloudEvents are candidates that haven't been evaluated yet.

The add-on packages live in this repository and are released with AvroSharp, all at the same version ([#77](https://github.com/AvroSharp/AvroSharp/issues/77)). Each depends on a tested range of its third-party library ([#83](https://github.com/AvroSharp/AvroSharp/issues/83)). For example, Confluent changed its serializer base classes in a minor release (2.14.0), so `AvroSharp.Confluent` depends on `[2.14.0, 3.0.0)`, and its tests run against both ends.

[The ecosystem spike](reviews/2026-09-30-ecosystem.md) has the measurements, the findings for each candidate, and why the others were left out.
