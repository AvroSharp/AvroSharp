# Ecosystem integrations spike (#78)

The timeboxed spike of [#78](https://github.com/zcsizmadia/AvroSharp/issues/78): whether an `AvroSharp.Confluent` package can be built on Confluent's own serializer classes, what it gains, and which other add-on packages to build. Done on 2026-09-30 against main at `9a5928c`.

## Summary

| Candidate | Decision | Why |
|---|---|---|
| `AvroSharp.Confluent` ([#79](https://github.com/zcsizmadia/AvroSharp/issues/79)) | **Go, first** | Works on Confluent's public base classes with no Apache.Avro; interoperates byte for byte; 8.7× faster to serialize and 6.5× to deserialize |
| `AvroSharp.KafkaFlow` | **Go, after Confluent** | Two small interfaces (MIT); today's Confluent Avro adapter builds Apache's serializer by reflection |
| `AvroSharp.Azure.SchemaRegistry` | **Go, lower priority** | No abstraction to plug into, but `SchemaRegistryClient` and `MessageContent` are enough (MIT) |
| `AvroSharp.Aws.Glue` | **Go, lower priority** | The wire format is already in AvroSharp; the official .NET SDK is a 113 MB native wrapper for Linux only |
| Pulsar, AWS Lambda Powertools, CloudEvents | Not now | Not verified in this spike; revisit after the Kafka packages |
| NServiceBus, Brighter, Wolverine, Rebus, Orleans | No | Almost no demand: NServiceBus's own survey issue is the only current signal |
| Streamiz, Silverback, MassTransit | Docs only | They wrap Confluent's serdes; `AvroSharp.Confluent` covers them |
| Event Hubs Capture | Docs only | `AvroFileReader` reads Capture files; the codec is not documented, so a sample should be tested on a real file |
| Iceberg manifests | After 1.0 | Needs runtime-built schemas with `field-id` properties and per-spec `data_file` records |

## AvroSharp.Confluent

### The prototype

`AvroSharpSerializer<T>` and `AvroSharpDeserializer<T>`, for `T : IAvroSerializable<T>` (.NET 8 and later), derive from Confluent's `AsyncSerializer<T, AvroSchema>` and `AsyncDeserializer<T, AvroSchema>`, with AvroSharp's `AvroSchema` as the parsed schema type. They reference only `Confluent.SchemaRegistry`, so an application no longer pulls in Apache.Avro. From Confluent's base classes they get:

- subject name strategies, auto-registration or lookup, `use.latest.version` and `use.latest.with.metadata`, and normalization;
- the schema ID strategies (the magic-byte prefix, or GUIDs and the `__value_schema_id` header) through `schemaIdEncoder`/`schemaIdDecoder`;
- writer schemas fetched by ID and parsed once (`GetWriterSchema`, cached by the base class), and references resolved recursively (`ResolveReferences`), which the prototype parses with one `AvroSchemaParser`, retrying each until the names it uses are known;
- rules: domain rules through `ExecuteRules` with a field transformer, and encoding rules (payload encryption) on the encoded bytes.

Everything the prototype needs is public or protected. The one internal type, `PrefixSchemaIdEncoder`, is the base class's default encoder, so it isn't needed directly.

### Interop, against Redpanda 25.2.1

Every check passed, with one class generated in the Apache.Avro compatibility mode, so the same `Order` goes through both serdes:

- AvroSharp writes, and Confluent's `AvroDeserializer<GenericRecord>` and `AvroDeserializer<Order>` read.
- Confluent's `AvroSerializer<Order>` writes, and AvroSharp reads.
- On one subject, both serializers register the same schema (one version) and write identical bytes (78 B).
- An older writer schema (without two fields) is read as the current type, with the defaults.
- A producer and a consumer round trip through Kafka with the serde plugged in (`SetValueSerializer`, and `SyncOverAsyncDeserializer`).
- A schema that references a type registered under another subject parses.

Testcontainers' default Redpanda image, v22.2.1, rejects registrations from Confluent's 2.15 client with `parse error at offset 854`, past the end of the schema in the request, while the same schema registers through a plain request. Most likely the client's newer request fields; tests need a current image.

### Performance

One `Order` with five items, BenchmarkDotNet 0.15.8, .NET 10, an i7-12800H pinned to 4 cores, with the registry's answers cached by Confluent's client (the hot path makes no requests):

| | Confluent (Apache.Avro) | AvroSharp prototype | |
|---|---|---|---|
| Serialize | 2,355 ns, 4.05 KB | 272 ns, 1.59 KB | 8.7× faster, 61% less allocated |
| Deserialize | 4,791 ns, 5.04 KB | 738 ns, 1.15 KB | 6.5× faster, 77% less allocated |

The prototype's serializer still allocates a 1 KB buffer per message; a pooled buffer takes most of the 1.59 KB away.

### Dependency range

The prototype needs `Confluent.SchemaRegistry` **2.14.0 or later**. Before 2.11 there are no schema ID strategies, and `SchemaId` is internal. 2.14.0 changed protected members of the base class, a binary break for a subclass, in a minor version:

| Version | Change to the serde base classes |
|---|---|
| 2.12.0 → 2.13.0 | `SchemaId` gains a constructor with message indexes (additive) |
| 2.13.0 → 2.14.0 | `GetSubjectName` returns `Task<string>` instead of `string`, and `subjectNameStrategy` is an `AsyncSubjectNameStrategyDelegate` (breaking) |
| 2.14.0 → 2.15.1 | none |

So the package should depend on `[2.14.0, 3.0.0)`, and CI should build and test it against the lowest and the newest Confluent version it allows ([#83](https://github.com/zcsizmadia/AvroSharp/issues/83)). A later break in a minor version then fails CI instead of an application.

### Work left for #79

- **Field-level rules (CSFLE):** Confluent's encryption executor calls the serde's field transformer. AvroSharp's `AvroValueTransformer` ([#82](https://github.com/zcsizmadia/AvroSharp/issues/82)) already walks the generic model by schema, with each field's path and properties; the package connects it to Confluent's `FieldTransformer`, and generated types reach it through the generic model (or a typed walker, if that is too slow). The prototype throws `NotSupportedException` instead of skipping them.
- **CEL rules:** Confluent's `CelExecutor` recognizes only Apache.Avro's `ISpecificRecord` and `GenericRecord`. Types generated in the compatibility mode work; for plain AvroSharp types, CEL rules are not supported unless Confluent's executor is extended. Document it.
- **Migration rules:** Confluent converts through JSON. AvroSharp has JSON readers and writers, so the same path works; the prototype throws.
- **`GenericRecord` and primitive types,** besides generated types, and a .NET Standard 2.0 path through `IAvroWritable`/`IAvroReadable` (the prototype uses the .NET 8 static interface).
- **Configuration:** read the same `avro.serializer.*` and `avro.deserializer.*` keys as Confluent's `AvroSerializerConfig`, so an application only changes the type.
- The rules and encryption packages are Apache-2.0, like Confluent's others, and don't depend on Apache.Avro.

## The other candidates

- **KafkaFlow** (MIT): `ISerializer.SerializeAsync(object message, Stream output, ISerializerContext context)` and `IDeserializer.DeserializeAsync(Stream input, Type type, ISerializerContext context)`. Its Confluent Avro adapter creates Confluent's `AvroSerializer<>` for each message type by reflection; an `AvroSharp.KafkaFlow` package wraps `AvroSharp.Confluent` the same way, without reflection for generated types. AWS's Glue SDK already ships KafkaFlow serializers this way.
- **Azure Schema Registry** (MIT): `SchemaRegistryAvroSerializer` is a concrete class over Apache.Avro with no interface to implement. The schema ID travels in the content type (`avro/binary+<id>`), not in the payload. A package uses `SchemaRegistryClient` to register and fetch schemas and sets `MessageContent`'s content type itself. `Microsoft.Azure.Data.SchemaRegistry.ApacheAvro` is still at 1.0.1, with about 1.7M downloads.
- **AWS Glue:** the framing (`0x03`, a compression byte, the 16-byte schema version UUID) is `AvroRegistryFraming.AwsGlue` already. The official `AWS.Glue.SchemaRegistry` package is a GraalVM native build of the Java serdes, published for linux-x64, linux-musl-x64 and linux-arm64 only, about 113 MB, and depends on Apache.Avro. A managed package needs `AWSSDK.Glue`'s `GetSchemaVersion`/`RegisterSchemaVersion` and a cache.
- **Iceberg:** manifests default to the `gzip` codec setting (`write.manifest.compression-codec`), most likely written as Avro's deflate codec (not verified here); their file metadata has `schema`, `partition-spec`, `partition-spec-id`, `format-version` and `content`. A reader needs custom schema properties (`field-id`, `element-id`, `key-id`, `value-id`) and schemas built at run time.
- **Event Hubs Capture:** records are `Microsoft.ServiceBus.Messaging.EventData` (`SequenceNumber`, `Offset`, `EnqueuedTimeUtc`, `SystemProperties`, `Properties`, `Body`). Microsoft's documentation doesn't state the codec, so a sample needs a real Capture file.
- **Messaging frameworks:** NServiceBus has an Avro sample and an open survey issue (Particular/NServiceBus#7397); Rebus, Orleans and Brighter show no current request for Avro.

## Sources

- Confluent's serde base classes and its Avro serde: decompiled from `Confluent.SchemaRegistry` and `Confluent.SchemaRegistry.Serdes.Avro` 2.15.1; the API differences by reflection over 2.12.0, 2.13.0, 2.14.0 and 2.15.1.
- KafkaFlow: https://github.com/Farfetch/kafkaflow (`src/KafkaFlow.Abstractions`, `src/KafkaFlow.Serializer.SchemaRegistry.ConfluentAvro`).
- Azure: https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/schemaregistry.
- AWS Glue: https://github.com/awslabs/aws-glue-schema-registry and https://docs.aws.amazon.com/glue/latest/dg/schema-registry-gs-serde-csharp.html.
- Iceberg: https://iceberg.apache.org/spec/ and `TableProperties.java`, `ManifestWriter.java` in https://github.com/apache/iceberg.
- Event Hubs Capture: https://learn.microsoft.com/azure/event-hubs/explore-captured-avro-files.
