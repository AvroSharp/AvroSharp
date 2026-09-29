# AvroSharp and Apache.Avro

[Apache.Avro](https://www.nuget.org/packages/Apache.Avro) is the Apache Software Foundation's C# library for Avro. AvroSharp is an independent implementation of the same [specification](https://avro.apache.org/docs/1.12.0/specification/), written from the specification for current .NET. The two read and write the same bytes: AvroSharp's interop tests check it against Apache.Avro 1.12.2 in both directions, and against files that Apache Avro Java wrote.

This page covers what AvroSharp does differently, where Apache.Avro is the better fit, and how to move code over.

On this page:
- [At a glance](#at-a-glance)
- [Speed and allocations](#speed-and-allocations)
- [Generated code instead of reflection](#generated-code-instead-of-reflection)
- [Following the specification](#following-the-specification)
- [Hostile input](#hostile-input)
- [More of the Avro ecosystem in one library](#more-of-the-avro-ecosystem-in-one-library)
- [When Apache.Avro is the better fit](#when-apacheavro-is-the-better-fit)
- [Moving from Apache.Avro](#moving-from-apacheavro)

## At a glance

| | AvroSharp | Apache.Avro 1.12.2 |
|---|---|---|
| Speed | faster on every benchmark: records 1.95–6.18×, container reads up to 22×, schema parsing up to 2.07× ([benchmarks](benchmarks.md)) | the baseline |
| Allocations | no more than Apache.Avro on any benchmark; a container file written with under 6 KB | 5.7 MB for the same container file |
| Code generation | a source generator: types are generated while the project builds ([guide](code-generation.md)); or the `avrosharp` tool ([CLI](cli.md)) | the `avrogen` tool |
| Serializers | generated, with no reflection; Native AOT and trimming compatible, checked by a Native AOT build in CI | specific reader and writer driven by the schema and reflection at run time |
| Codecs | null, deflate, snappy, zstandard, bzip2, xz: two packages, fully managed | null and deflate, and a satellite package per codec |
| Schema registries | Confluent, Confluent GUID, Apicurio and AWS Glue framing, with no client dependency | not included |
| Specification | follows it where Apache.Avro 1.12.2 deviates ([details](#following-the-specification)) | five deviations pinned by AvroSharp's tests |
| Hostile input | bounded memory and nesting for malformed or hostile data and schemas; fuzzed nightly | — |
| Targets | .NET 8, 9 and 10, .NET Standard 2.0 and 2.1 (so .NET Framework) | .NET Standard 2.0 and 2.1 |
| License | MIT | Apache 2.0 |
| Maturity | an early preview (0.x); the API may change before 1.0 | the long-standing reference implementation |

## Speed and allocations

A release requires every AvroSharp benchmark to be faster than its Apache.Avro counterpart and to allocate no more ([the performance gate](benchmarks.md#the-performance-gate)). On the latest full run (i7-12800H, .NET 10):

- **Generated records** read 3.95× and write 6.18× faster; the generic model reads 1.95× and writes 3.93× faster.
- **Container files** read 2.9–7.4× faster with the fast codecs, and up to 22× with xz; writes are 3.5–10× faster.
- **Reading an older schema version** is 2.72× faster with generated code.
- **Writes allocate close to nothing**: under 6 KB for a whole container file, against 5.7 MB.

Where the gain comes from:
- **Generated serializers** call the writer and reader directly, in schema order, with no schema walk, boxing or virtual calls per value.
- **Spans and buffer writers**: `AvroWriter` and `AvroReader` work over `Span<byte>`, `IBufferWriter<byte>` and `ReadOnlySequence<byte>`, with pooled buffers and no per-value streams.
- **A value type for generic data**: `AvroValue` holds any Avro value without boxing, and arrays of primitives are stored as the primitives.
- **Fast primitives**: varints decoded and encoded without a loop per byte where the CPU allows, and bulk array reads, each kept only if it wins on every tested CPU ([rules](benchmarks.md#rules-for-fast-paths)).

The [benchmarks page](benchmarks.md) has the full table and how to run it yourself.

## Generated code instead of reflection

AvroSharp's [source generator](code-generation.md) turns `.avsc` files into C# types as the project builds: there is no separate generation step to run and no generated code to check in, and the types update as the schemas change. Each record gets serializers that write and read its fields directly, so they work with Native AOT and trimming; CI publishes a Native AOT app and fails on any trim or AOT warning.

Generated types also get what hand-written code usually adds around Avro:
- `ToAvroBytes()`/`FromAvroBytes()`, and writing into caller memory without allocating (`TryWriteAvroBytes`);
- reading into an existing instance, reusing its collections (`ReadFrom`);
- schema defaults applied by the constructor;
- `DateOnly`, `TimeOnly`, `DateTimeOffset`, `Guid` and `decimal` for logical types, with decimals written exactly or rejected, never rounded;
- `IAvroSerializable<T>` on .NET 8 and later, so files, streams and messages take the type with no delegates.

## Following the specification

AvroSharp follows the Avro 1.12 specification, and matches Apache Avro Java where the specification leaves a choice. Where Apache.Avro 1.12.2 (C#) differs from the specification, AvroSharp's tests pin Apache's behavior, so a change in a later Apache release is noticed:

| Case | Specification and AvroSharp | Apache.Avro 1.12.2 |
|---|---|---|
| `uuid` on `fixed(16)` | a valid logical type (1.12) | rejects the schema |
| `fixed` of size 0 | valid | rejects it |
| a `fixed` with a logical type, in canonical form | the fixed definition stays | the definition is dropped, leaving only the name |
| the empty namespace `""` inside a namespace | the null namespace | inherits the enclosing namespace |
| a null-namespace type referenced from inside a namespace | found, as in Java | an undefined name |

Also checked against Java:
- **Canonical form and fingerprints** pass all 34 of Apache's test vectors.
- **`ToJson()`** writes a schema as Java's `Schema.toString()` does, byte for byte (attribute order, and numbers as Java prints them), so registering AvroSharp's text finds the version a Java client registered.
- **Schema resolution** picks union branches by full name, then by unqualified name, then by promotion, as Java does, and applies reader aliases before names.
- **Container files** from Java, in every codec, are read, and Java reads the ones AvroSharp writes.

## Hostile input

Avro data often comes from outside the process: files, messages, and schemas embedded in them. AvroSharp bounds what malformed or hostile input can make it do, and reports it as `AvroDataException` or `AvroSchemaException`:
- **Memory:** every length is checked against the remaining input before anything is allocated; container blocks, schemas in file headers, stream objects and zero-size values have limits.
- **Nesting:** records, arrays and maps are limited in depth, and the thread's stack is checked, so a hostile schema cannot overflow the stack.
- **Codecs:** a corrupt block ends in `InvalidDataException`, never in a library's internal exception.
- **Fuzzing:** [libFuzzer targets](../fuzz/README.md) cover the readers, and run every night.

The limits are options (`GenericDatumReaderOptions`, `AvroFileReaderOptions`, `AvroStreamOptions`) with defaults that fit ordinary data.

## More of the Avro ecosystem in one library

- **Every codec in the specification**: `null` and `deflate` in AvroSharp, and snappy, zstandard, bzip2 and xz in [AvroSharp.Codecs](https://www.nuget.org/packages/AvroSharp.Codecs), on fully managed libraries with no native binaries.
- **Schema registries**: Confluent (4-byte ID and GUID), Apicurio and AWS Glue wire framing, with an ID resolver you supply, and no registry client dependency ([guide](../README.md#schema-registries)).
- **Single-object encoding** with a schema store selecting the writer schema by fingerprint ([guide](../README.md#single-object-encoding)).
- **Streams of objects** without a container, for sockets and pipes ([guide](../README.md#streams-of-objects)).
- **Asynchronous container files**, with no synchronous I/O, and pipelined reading that decompresses the next block while the current one is decoded.
- **JSON encoding** of the generic model, checked against Apache.Avro's JSON encoder and decoder.
- **Canonical forms and fingerprints** (CRC-64-AVRO, MD5, SHA-256), also from the command line ([CLI](cli.md#schema-fingerprint)).

## When Apache.Avro is the better fit

- **You need a 1.x API today.** AvroSharp is an early preview: the API may change before 1.0.
- **You serialize classes that have no schema file.** Apache.Avro's reflect API derives schemas from existing classes; AvroSharp generates classes from schemas, not the other way around.
- **You want the Apache Software Foundation's implementation**, maintained alongside the other Avro languages.

## Moving from Apache.Avro

The two libraries interoperate on the wire, so services can move one at a time. Within one codebase:

1. **Generate types that work with both.** Reference `AvroSharp.Generators` and set `<AvroSharpApacheCompatible>true</AvroSharpApacheCompatible>`. The generated classes replace avrogen's: code written for them compiles unchanged (the same property names, `_SCHEMA` and `Schema`), and Apache's `SpecificDatumWriter<T>`/`SpecificDatumReader<T>` still accept them. See [the compatibility mode](code-generation.md#migrating-from-avrogen-the-apacheavro-compatibility-mode).
2. **Move call sites one at a time** to AvroSharp's serializers: `order.ToAvroBytes()`, `AvroFileWriter.Create<Order>(stream)`, `AvroFileReader.Open<Order>(stream)`, `AvroMessage`, `AvroRegistryMessage`.
3. **Drop the compatibility mode and the Apache.Avro reference** when nothing uses Apache's API any more. The types then use .NET types for logical types (`DateOnly`, `decimal`) and, by default, PascalCase properties; set `AvroSharpPropertyNames` to `avro` to keep the field names.

If you check in generated code, the [`avrosharp` tool](cli.md#coming-from-avrogen) takes the place of `avrogen`, with `--apache-compatible` for step 1.

---

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
