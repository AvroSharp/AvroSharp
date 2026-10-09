# AvroSharp — Design

> **This is the design proposal, kept as a record of the reasoning.** The live milestone status is in [roadmap.md](roadmap.md). Where the text below differs from the repository, the repository is right. The known differences are in the table after the decisions.

> **Decisions made after this proposal (these take precedence over the text below):**
>
> - **Tests use TUnit**, not xUnit. The test projects cover net8.0/net9.0/net10.0 and, on Windows, most also net481 (the netstandard2.0 build on .NET Framework).
> - **No reflection on serialization paths.** The source generator is the typed-serialization path. The expression-tree and reflection tiers in Sections 4.5 and 8 are not part of v1; if a runtime fallback is ever added it ships as a separate opt-in package.
> - **Codecs use fully managed libraries only.** v1.0 ships every specification codec: deflate, snappy (Snappiest on net8+, Snappier on netstandard; both BSD-3-Clause), bzip2 (SharpZipLib, MIT), xz (Lzma.Net ≥ 5.8.4.3 (the first strong-named release), 0BSD; targets netstandard2.0/2.1 and net8.0-10.0) and zstandard (ZstdSharp.Port, MIT). Open point: the BCL `DeflateStream` uses the native zlib bundled with the runtime.
> - **Name AvroSharp, MIT license** (Section 13).
> - **One codec package, `AvroSharp.Codecs`**, not one package per codec: a reader of files whose codec is not known in advance needs one reference (2026-09-27).

**Status vs. design** (2026-10-02):

| Section | Proposed | In the repository |
|---|---|---|
| §3 Packages | Expression-tree and reflection typed paths in `AvroSharp`; `AvroSharp.Codecs.Snappy`/`.Zstd`/`.Bzip2`/`.Xz` | No reflection tiers (decision above); one `AvroSharp.Codecs` package. `AvroSharp.Tool` ships the `avrosharp` command (`gen`, and `schema canonical`, `fingerprint` and `compat`); `AvroSharp.Idl` doesn't exist yet (#174). The add-on packages (`AvroSharp.Confluent` and the others in §3) live in this repository. |
| §4.2, §4.7, §4.8 API names | Fingerprints as `ulong` and `ReadOnlyMemory<byte>`; an `ICodec` interface and a `CodecRegistry`; `AvroMessage.Encode`, `TryDecodeHeader` and an `ISchemaStore` | Fingerprints are `long` ([`AvroSchema.Fingerprint64`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.Fingerprint64.html), [`SchemaFingerprint.Crc64Avro`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.Crc64Avro.html)), and [`SchemaFingerprint.Md5`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.Md5.html) and [`Sha256`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.Sha256.html) return `byte[]`. Codecs derive from the abstract class [`AvroCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.html); a reader is given the codecs besides null and deflate in [`AvroFileReaderOptions.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReaderOptions.Codecs.html). Single-object messages are written by [`AvroMessage.Write`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.Write.html) and read by an [`AvroMessageReader<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessageReader-1.html) over an [`IAvroSchemaResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaResolver.html); [`AvroMessage.TryReadHeader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.TryReadHeader.html) reads the header. §8 shows the current API. |
| §4.4 Generic model | `GenericEnum`; typed accessors (`GetInt32`) and an `object?` indexer on `GenericRecord` | An enum value is an [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html) ([`AvroValue.FromEnum`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.FromEnum.html)); [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html)'s indexers and `TryGetValue` take and return `AvroValue`s. |
| §3, §4.10 I/O | `PipeReader`/`PipeWriter` overloads | `Stream`, `ReadOnlySpan<byte>`, `ReadOnlySequence<byte>` and `IBufferWriter<byte>`; the core doesn't reference `System.IO.Pipelines`. |
| §9 SDK | `global.json` 10.0.401 with `latestPatch`; `ImplicitUsings=enable`; `.globalconfig` | 10.0.100 with `latestFeature`; `ImplicitUsings=disable`; analyzer severities in `.editorconfig`. |
| §9 Projects | `Spec.Tests`, `Property.Tests`, `CodeGen.Tests`, `NetFramework.Tests`, `samples/` | Spec vectors and property tests live in `AvroSharp.Tests` and `AvroSharp.Interop.Tests`, code generation tests in the `Generators.*` projects, and net481 is a target of the test projects. `samples/` has twelve runnable samples, run by CI. |
| §9 Workflows | `release.yml`, `codeql.yml` | `ci.yml`, `codeql.yml`, `release.yml`, `docs.yml`, `fuzz.yml` (#76, #74, #34) and `upstream.yml` (weekly, the add-ons against the newest Confluent 2.x, #211). CI runs on Linux and Windows, x64 and Arm64 (#72). |
| §10 Tests | xUnit v3 | TUnit on Microsoft.Testing.Platform. |
| §13 Test data | `tests/TestData/apache/` | `tests/TestData/apache-avro/` (Apache's files), `tests/TestData/java-avro/` (files written by Apache Avro Java, including every logical type), `tests/TestData/aws-glue/` (messages written by AWS Glue's library) and `tests/TestData/schema-references/` (schemas with references, as Java writes them). |

Legend: **[src]** = verified by reading the reference source/page during this study; **[docs]** = reasoned from documentation/spec text; **[goal]** = unmeasured target, not a claim.

## 0. TL;DR (recommendations up front)

- **Name**: `AvroSharp` (root namespace `AvroSharp`, package prefix `AvroSharp.*`). The NuGet flat-container lookup for `avrosharp` returned 404 today, i.e. the ID appears unused **[src]** — re-check on nuget.org and push a 0.0.1-alpha reservation in M0.
- **License**: **MIT**. Apache.Avro is Apache-2.0, Chr.Avro is MIT **[src]**. Clean-room implementation from the spec; see Section 13.
- **Typed-serialization default**: Roslyn incremental **source generator** (AOT/trim-safe, fastest) as the primary path on all TFMs; **runtime expression-tree** compilation as the fallback/"no-generator" path; **reflection-only interpreter** for netstandard2.0/AOT-hostile dynamic scenarios and as the correctness oracle in tests.
- **Hard release gate**: every scenario in the benchmark matrix (Section 5) must beat Apache.Avro 1.12.x on throughput and be at or below it on allocations. Checked by local benchmark runs, on request (see Section 5.4); not run in CI. One exception was decided later: single-value writes on Zen+ CPUs (Section 11, SIMD rules, #168).
- **v1 scope**: schema model/parser/canonical form/fingerprints, binary encoding, generic + typed (source-gen and expression-tree) serialization, full writer/reader resolution, object container files (sync + async, null/deflate/snappy/zstd/bzip2/xz via satellite packages), single-object encoding, JSON encoding, schema-from-type, codegen (CLI + build-time generator). IDL (.avdl) and Confluent Schema Registry integration ship in v1.x, not v1.0. *Shipped:* IDL is still 1.x (#174), but Confluent and the other registry add-ons (§3) ship in 1.0.0; expression-tree serialization was dropped (decisions above).

---

## 1. What the references teach us

### 1.1 Chr.Avro (ch-robinson/dotnet-avro) **[src]**
- **Status**: repository is archived; last push 2026-07-02; README says "no longer actively maintained ... consider Apache.Avro". MIT. Last NuGet 10.13.1.
- **TFMs**: `netstandard2.0;net6.0` (Chr.Avro, Chr.Avro.Binary). Depends on `System.Collections.Immutable`, `Microsoft.CSharp`, `System.ComponentModel.Annotations`.
- **Architecture**: immutable-ish schema model; `SchemaBuilder` builds Avro schemas from CLR types via pluggable "cases"; `BinarySerializerBuilder`/`BinaryDeserializerBuilder` implement an ordered case list (logical types → primitives → array/map → enum → record → union), each case returns an `Expression`, and `Expression.Lambda<BinarySerializer<T>>(...).Compile()` produces the delegate. Custom cases are prepended.
- **Low-level I/O**: `BinaryWriter` is a **sealed class over `Stream`**, using `stackalloc` on net6+ and heap allocation on netstandard2.0. `BinaryReader` is a **`ref struct` over `ReadOnlySpan<byte>`** (zero-alloc varint/fixed reads; strings via `Encoding.UTF8.GetString`).
- **Strengths**: excellent API ergonomics (`SchemaBuilder`, one-line serializer creation), correct and well-tested type mapping, first-class Confluent integration, pluggable cases.
- **Weaknesses**: expression-tree `Compile()` is not NativeAOT-compatible and on netstandard2.0 (.NET Framework) it either JITs or falls back to the slow interpreter; writer over `Stream` (no `IBufferWriter<byte>`); no async, no container-file support at all; deserializer builder maps *one* schema to a type — there is no explicit writer/reader schema resolution stage visible in the builder (resolution is implicit through type matching); codegen is "experimental"; abandoned.
- **Adopt**: case-based extensibility concept (as a `ITypeMapping` registry, not expression cases), `SchemaBuilder`-style type→schema, Confluent integration shape, ref-struct reader over spans.
- **Improve**: writer becomes ref-struct over `IBufferWriter<byte>`/`Span<byte>`; add explicit resolution; make source-gen the default; add container files.
- **Avoid**: expression trees as the *only* typed path; `Stream`-centric writer; `Microsoft.CSharp`/`dynamic` dependency.

### 1.2 Apache.Avro (apache/avro lang/csharp) **[src]**
- **Version/TFMs**: NuGet 1.12.2 (2026-08-18), `netstandard2.0;netstandard2.1` only; depends on `Newtonsoft.Json` and `System.CodeDom` (for avrogen). Strong-named, StyleCop + NetAnalyzers.
- **Schema**: abstract `Schema` + `Type` enum (16 tags) parsed from Newtonsoft `JToken`; immutable after construction; `PropertyMap` for custom props; `SchemaNormalization.ToParsingForm` + `Fingerprint64` (CRC-64-AVRO, `Empty64 = -4513414715797952619` = `0xC15D213AA4D7A795`) + MD5/SHA-256.
- **Encoder/Decoder**: `BinaryEncoder`/`BinaryDecoder` are **classes over `Stream`**. `WriteString` does `Encoding.UTF8.GetBytes(string)` (allocates) then `WriteBytes`; doubles are written byte-by-byte; `BinaryDecoder` reads varints via `stream.ReadByte()` one byte per call and allocates a **new `byte[]` per `ReadBytes`**. No `Span<T>`, no async.
- **Readers**: `DefaultReader` (used by `GenericReader`/`ReflectReader`) **re-resolves writer vs reader schema on every `Read` call** by switching on `writerSchema.Tag`; `PreresolvingDatumReader` (used by `GenericDatumReader`/`SpecificDatumReader`) pre-builds a tree of `delegate object ReadItem(object reuse, Decoder dec)` — better, but every primitive is boxed through `object`. `GenericRecord` stores `object[]` (boxing all value types). `ReflectDefaultReader` → `DotnetProperty.GetValue/SetValue` call **`PropertyInfo.GetValue/SetValue`** directly (uncached reflection, boxed).
- **Container files**: `DataFileReader` allocates a `byte[]` per block, `codec.Decompress()` returns a new `byte[]`, wraps it in a `MemoryStream`; `DataFileWriter` writes into `MemoryStream`s and `DeflateCodec.Decompress` creates two `MemoryStream`s. No async anywhere.
- **Strengths**: reference implementation, spec-complete (resolution, container files, codecs as satellite packages, IPC/protocols, avrogen), interop-tested against other languages, actively maintained.
- **Weaknesses**: per-call allocations on every hot path; boxing everywhere in Generic/Reflect; Stream-only sync I/O; Newtonsoft dependency; CodeDom-based avrogen emits dated C# (public fields/props, no nullable, no records); no AOT story (reflection + CodeDom); no net8+ TFM so no modern BCL intrinsics.
- **Adopt**: feature surface (this is the compatibility target), pre-resolved reader tree idea (but strongly typed), spec test data (`share/test/data`: `weather*.avro` in null/deflate/snappy/zstd, `schema-tests.txt` with `<<INPUT/<<canonical/<<fingerprint` entries, `messageV1/` single-object interop files).
- **Improve**: every hot path listed in Section 5.1.
- **Avoid**: Stream-centric encoder/decoder, boxing generic model, Newtonsoft, CodeDom, per-read resolution.

---

## 2. Goals, non-goals, positioning

**Goals (v1)**
1. Spec-complete Avro 1.12 for schemas, binary encoding, JSON encoding, resolution, container files, single-object encoding, canonical form, fingerprints, all logical types (incl. `big-decimal`, `timestamp-nanos`, `local-timestamp-*`).
2. Byte-for-byte interop with Apache.Avro (C#, and therefore Java) — proven by tests, not asserted.
3. Faster and lower-allocating than Apache.Avro on every benchmark scenario (release gate).
4. NativeAOT- and trimming-clean core (`IsAotCompatible=true`) with source-generated typed serialization.
5. Async-first I/O (`PipeReader/PipeWriter`, `IAsyncEnumerable<T>`) with sync `Stream` parity.
6. Modern codegen (records, nullable, DateOnly/TimeOnly/Guid, partial types, DU-shaped unions).
7. Engineering hygiene from commit 1.

**Non-goals (v1)**: Avro RPC/IPC (protocols are *parsed* for codegen but no transport); Avro IDL (.avdl) parsing (v1.x); Confluent Schema Registry client (v1.x, separate package); schema-registry-aware Kafka serdes; Trevni. (Changed: the registry serdes ship in 1.0 as add-on packages, on the registries' own clients; see §3.)

**Positioning**: "The Avro library you'd write in 2026": Apache.Avro's completeness + Chr.Avro's ergonomics, on `System.Text.Json`, `Span`, `Pipelines`, source generators, and NativeAOT — with Chr.Avro abandoned there is a real gap for a modern, high-performance, maintained alternative.

---

## 3. Package / assembly layout

| Package | Contents | v1.0 |
|---|---|---|
| `AvroSharp` | Schema model/parser/writer (STJ), canonical form + fingerprints, `AvroWriter`/`AvroReader` ref structs, generic model, resolution, expression-tree + reflection typed paths, container files (Stream + Pipelines), single-object encoding, JSON encoding, null/deflate codecs (BCL `DeflateStream`), `SchemaBuilder` (type→schema), attributes | Yes |
| `AvroSharp.Abstractions` | *Not a separate package.* Attributes and core interfaces live in `AvroSharp` — one dependency is a feature. | — |
| `AvroSharp.Generators` | Roslyn incremental generator(s): (a) `.avsc/.avpr` AdditionalFiles → C# types; (b) `[AvroSerializable]` attribute → serializer/deserializer/schema. `netstandard2.0`, analyzer package. | Yes |
| `AvroSharp.CodeGen` | Shared codegen engine (schema → C# model → text), used by CLI + generator. netstandard2.0 (must run inside Roslyn). | Yes |
| `AvroSharp.Tool` | `dotnet tool` (`dnx AvroSharp.Tool gen ...`), System.CommandLine 2.0 GA. | Yes |
| `AvroSharp.Codecs` | snappy (Snappiest on net8+, Snappier on netstandard, with the CRC-32 trailer), zstandard (ZstdSharp.Port), bzip2 (SharpZipLib) and xz (Lzma.Net) in one package, so a reader of files of unknown codec needs one reference (decided 2026-09-27; was one package per codec) | Yes |
| `AvroSharp.Idl` | `.avdl` parser → protocol/schema model | v1.x |
| `AvroSharp.Confluent` | Confluent.Kafka serializers on Confluent's serde base classes and `Confluent.SchemaRegistry`'s client. The wire format is in the core package (`AvroRegistryFraming`). | 1.0 (first published in 1.0.0) |
| `AvroSharp.KafkaFlow`, `AvroSharp.Azure.SchemaRegistry`, `AvroSharp.Aws.Glue`, `AvroSharp.Aws.Glue.Kafka` | KafkaFlow middleware, Azure Schema Registry, and AWS Glue Schema Registry (with its Confluent.Kafka serializers), released with AvroSharp at the same version (see [integrations](integrations.md)) | 1.0 (first published in 1.0.0) |
| `AvroSharp.MSBuild` | *Not needed*: build-time generation is the source generator. | — |

Brotli is not an Avro codec; do not ship it. Deflate uses BCL only, so the core package has zero third-party runtime dependencies on net8+ (netstandard needs `System.Text.Json` and `System.Threading.Channels`, and netstandard2.0 also `System.Memory` and `Microsoft.Bcl.AsyncInterfaces`).

---

## 4. Core architecture

### 4.1 Schema model
- `abstract class AvroSchema` (sealed leaf types: `NullSchema`, `BooleanSchema`, `IntSchema`, `LongSchema`, `FloatSchema`, `DoubleSchema`, `BytesSchema`, `StringSchema`, `RecordSchema`, `EnumSchema`, `ArraySchema`, `MapSchema`, `UnionSchema`, `FixedSchema`) + `LogicalSchema` wrapping an underlying schema with a `LogicalType` object (`DecimalLogicalType(precision, scale)`, `BigDecimalLogicalType`, `UuidLogicalType`, `DateLogicalType`, `TimeMillis/Micros`, `TimestampMillis/Micros/Nanos`, `LocalTimestampMillis/Micros/Nanos`, `DurationLogicalType`, `UnknownLogicalType(name)` — spec says unknown logical types must degrade to the underlying type).
- Fully immutable: `ImmutableArray<Field>`, `FrozenDictionary<string,int>` field index (net8+; `Dictionary` on netstandard), `SchemaName` value type (name, namespace, fullname), `Aliases`, `Doc`, `CustomProperties` as `IReadOnlyDictionary<string, JsonElement>`.
- Recursive schemas: parser uses a two-phase approach (register named schema placeholder → fill fields) so `RecordSchema.Fields` can reference itself; immutability preserved via an internal builder that is sealed after parse.
- `RecordSchema` precomputes: field positions, default-value `AvroValue`s (decoded once), `IsSimple` (all-primitive fast path).
- Each `AvroSchema` caches its parsing canonical form and CRC-64-AVRO fingerprint lazily (`Lazy<T>`-free, `Interlocked`-guarded).

### 4.2 Parser / writer (System.Text.Json)
- `AvroSchema.Parse(ReadOnlySpan<byte> utf8Json)`, `Parse(string)`, `Parse(ref Utf8JsonReader, SchemaParseOptions)`; single pass over `Utf8JsonReader` with an explicit `NameResolutionScope` (encoding namespace stack, per the spec's namespace inheritance rules) — no DOM for the common path; `JsonDocument` is only used to capture unknown properties and default values (which are stored as `JsonElement` clones).
- Options: `Strict` (spec-conformant name validation, default) vs `Permissive` (Apache Java's `validateNames=false` behaviour for legacy schemas), and `DefaultValueValidation`.
- `ToJson(JsonWriterOptions)` writes via `Utf8JsonWriter`; `ToCanonicalString()` implements all PCF transforms in spec order ([PRIMITIVES], [FULLNAMES], [STRIP], [ORDER], [STRINGS], [INTEGERS], [WHITESPACE]) **[docs]**.
- Fingerprints: `SchemaFingerprint.Crc64Avro(schema)` (table generated from `EMPTY = 0xC15D213AA4D7A795` **[src, matches Apache Empty64]**), `Md5`, `Sha256` — return `ulong`/`ReadOnlyMemory<byte>`; verified against `schema-tests.txt`.

### 4.3 Low-level encoding: `AvroWriter` / `AvroReader`
```csharp
public ref struct AvroWriter   // over IBufferWriter<byte>, with a local Span<byte> window
{
    public AvroWriter(IBufferWriter<byte> output);
    public void WriteNull(); WriteBoolean(bool); WriteInt(int); WriteLong(long);
    public void WriteFloat(float); WriteDouble(double);
    public void WriteString(string); WriteString(ReadOnlySpan<char>); WriteUtf8(ReadOnlySpan<byte>);
    public void WriteBytes(ReadOnlySpan<byte>); WriteFixed(ReadOnlySpan<byte>);
    public void WriteArrayStart(int count); WriteArrayEnd(); WriteMapStart(int count); WriteMapEnd();
    public void WriteUnionIndex(int); WriteEnum(int);
    public void Flush(); public long BytesWritten { get; }
}
public ref struct AvroReader   // over ReadOnlySpan<byte> (fast) or ReadOnlySequence<byte> (multi-segment)
{
    public AvroReader(ReadOnlySpan<byte> data);
    public AvroReader(in ReadOnlySequence<byte> data);
    public int ReadInt(); long ReadLong(); ... ; string ReadString(); void ReadString(IBufferWriter<char>); 
    public ReadOnlySpan<byte> ReadBytesSpan(); byte[] ReadBytes(); void ReadFixed(Span<byte> dest);
    public long ReadArrayBlock(out long byteSize); ... ; void SkipValue(AvroSchema);
    public long Consumed { get; } public bool IsEmpty { get; }
}
```
*Shipped names differ* (see the [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html) and [`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) reference): blocks are `WriteBlockCount`/`WriteBlockEnd` and `ReadBlockCount`; UTF-8 strings `WriteStringUtf8`/`ReadStringUtf8`; position `BytesConsumed`, `BytesRemaining` and `IsAtEnd`. The reader has no `SkipValue(AvroSchema)`: skipping by schema is done by the readers' plans, over `SkipBytes`, `SkipVarint` and `SkipRaw`.
- Varint: unrolled fast path for 1–2 byte values, `BitOperations.LeadingZeroCount` to size the write in one `Span` store on net8+; reads validate ≤10 bytes and reject over-long encodings.
- Floats via `BinaryPrimitives.Write*LittleEndian` (netstandard2.0: `BitConverter` + `BinaryPrimitives` from System.Memory).
- Strings: `Encoding.UTF8.GetByteCount` → varint → `GetBytes(span)` directly into the writer window; reader uses `Encoding.UTF8.GetString(ReadOnlySpan<byte>)` and, on net8+, `Utf8.ToUtf16` for `IBufferWriter<char>`. Optional `StringPool`/interning hook for enum-like strings.
- Reader over `ReadOnlySequence<byte>`: the fast path always reads from the current span; slow path (`ReadMultiSegment`) only on boundary crossing — this is the same pattern as `Utf8JsonReader`. Container-file blocks are always fully materialized in one pooled buffer, so single-span is the common case.
- Skipping: `SkipValue(schema)` uses byte-size hints for arrays/maps blocks written with negative counts (we always write negative-count blocks for arrays/maps of non-primitive items so consumers can skip them — spec-permitted **[docs]**).

### 4.4 Generic data model
- `GenericRecord` (sealed class): `AvroSchema Schema`, values stored in a **`AvroValue[]`** where `AvroValue` is a 16-byte struct (`long`/`double` bits + `object?` ref + type tag) — no boxing for primitives, unlike Apache's `object[]` **[src]**. Indexers by position and name; `TryGetValue<T>`; `GetInt32(int pos)` typed accessors; `object?` indexer kept for convenience (boxes on demand).
- `GenericEnum` (schema + ordinal, symbol string), `GenericFixed` (schema + `byte[]`/`ReadOnlyMemory<byte>`), arrays as `List<T>`/`AvroValue[]` (arrays of `boolean`, `int`, `long`, `float` and `double` items hold the primitives themselves: they are read in bulk, and booleans and floating-point items are written as one copy; #23, #26), maps as `Dictionary<string, AvroValue>`.
- [`GenericDatumWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriter.html)/[`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html) are built once per schema (the resolving reader once per writer and reader schema pair) into a tree of private writer or reader nodes, one per schema node, that read/write `AvroValue` without boxing. (The proposal called them typed `IAvroCodec` nodes. `IAvroValueSerializer<T>`, the name `IAvroCodec<T>` took in #134, is the interface generated code uses for its values; the generic model does not use it.)

### 4.5 Typed serialization strategy — comparison and recommendation

| Criterion | Reflection interpreter | Expression trees (Chr.Avro) | Roslyn source generator |
|---|---|---|---|
| Speed | slow (boxing, PropertyInfo) | near hand-written after JIT | hand-written (best); inlinable |
| NativeAOT / trimming | needs `DynamicallyAccessedMembers`, boxing OK but slow | `Compile()` unsupported on AOT (falls to interpreter or throws); IL-emit unavailable | fully supported |
| netstandard2.0 / .NET Framework | works | works (JIT); no `ref struct` in expression lambdas is a pain — Chr.Avro passes `BinaryReader` by `ref` **[src, delegate takes `context.Reader`]** but ref-struct params inside expression trees have restrictions | works; generated code is plain C# targeting netstandard2.0 |
| Startup | none | ~ms per type (compile) | none |
| Dynamic types (runtime Type, no attributes) | yes | yes | no |
| Ergonomics | zero-setup | zero-setup | `[AvroSerializable]` partial + build |

**Recommendation**: three-tier `AvroTypeResolver`:
1. **Source-generated** (`AvroSharp.Generators`) — default for user POCOs; emits `AvroSerializer<T>` implementations calling `AvroWriter`/`AvroReader` directly with resolved-schema switch tables. Also emits the schema and a `IAvroSerializable<TSelf>` static abstract member (`static abstract AvroSchema Schema { get; }`, C# 11 on net8+; regular static on netstandard).
2. **Runtime-compiled** (`AvroSharp` core, `AvroSerializer.CreateDynamic<T>()`) — expression trees, `[RequiresDynamicCode]`-annotated so the AOT analyzer warns; used by ASP.NET/Kafka apps that don't want a generator step. Delegate shape: `delegate void Serialize<T>(ref AvroWriter w, in T value)`; expression lambdas over ref structs are feasible on net8+ because `ref struct` parameters are allowed in expression lambdas via `Expression.Parameter(typeof(AvroWriter).MakeByRefType())`. **Dropped** (see M4): serialization paths use no reflection, so this tier and its M2 spike are not planned.
3. **Reflection interpreter** — `[RequiresUnreferencedCode]`, used as the test oracle and as the last resort.

All three produce the same `AvroSerializer<T>` abstract class so the file/single-object/Confluent layers are strategy-agnostic.

### 4.6 Schema resolution
- `SchemaResolver.Resolve(writer, reader) -> ResolvedSchema` computed **once**, cached in a `ConcurrentDictionary<(writerFp, readerFp), ResolvedSchema>`. The `ResolvedSchema` is a tree of `ResolvedNode` with kinds: `Identity`, `Promote(int→long|float|double, long→float|double, float→double, string↔bytes)`, `RecordReorder(fieldMap[], defaults[], skips[])`, `UnionToUnion(indexMap[])`, `UnionToNonUnion`, `NonUnionToUnion(branch)`, `EnumRemap(symbolMap[], default)`, `ArrayOf(node)`, `MapOf(node)`, `Error(reason)` (deferred so a mismatched union branch only throws when encountered, as in Java).
- The generic reader and the source-generated readers both consume `ResolvedSchema`: the generator emits a `switch` over a small `ReaderPlan` (field order permutation + skip list) so evolution needs no runtime codegen. Writer-field skipping uses `AvroReader.SkipValue` (shipped as skip nodes in the plans; see the note in §4.3).
- Aliases are honored for named types and fields per spec **[docs]**.

### 4.7 Object container files
- `AvroFileWriter<T>`: `Create(Stream|PipeWriter|IBufferWriter<byte>, AvroSchema, AvroFileWriterOptions{Codec, SyncInterval=64KiB, Metadata, SyncMarker})`; `Write(in T)` encodes into a pooled `ArrayBufferWriter`-like `PooledBufferWriter` (ArrayPool-backed); on threshold, codec compresses span→`IBufferWriter<byte>` (no `byte[]` returns), block header written with `AvroWriter`, sync marker appended; `FlushAsync(CancellationToken)` and `DisposeAsync`. Sync marker from `RandomNumberGenerator.Fill`.
- `AvroFileReader<T>`: `Open(Stream|PipeReader|ReadOnlySequence<byte>, readerSchema?, options)`; header parsed with `AvroReader`; blocks are read into a pooled buffer, decompressed into a second pooled buffer (`ICodec.Decompress(ReadOnlySpan<byte>, IBufferWriter<byte>)`), records decoded from a single span; sync marker validated (`Sync()`/`Seek()` supported for splittable reads, `PastSync`). API: `IAsyncEnumerable<T> ReadAllAsync(ct)`, `IEnumerable<T> ReadAll()`, block-level `ReadBlockAsync()` returning `AvroBlock` (count + memory) for parallel decode, and `Header`/`Metadata`/`WriterSchema`.
- `ICodec` interface: `string Name`; `void Compress(ReadOnlySpan<byte>, IBufferWriter<byte>)`; `void Decompress(ReadOnlySpan<byte>, IBufferWriter<byte>)`; `int MaxCompressedLength(int)`. Registry `CodecRegistry` with null/deflate built in; satellite packages register via `CodecRegistry.Register`. Snappy codec appends big-endian CRC-32 of uncompressed data per spec **[docs]**.

### 4.8 Single-object encoding
`AvroMessage.Encode<T>(in T, IBufferWriter<byte>)` writes `C3 01` + 8-byte little-endian CRC-64-AVRO + payload **[docs]**; `AvroMessage.TryDecodeHeader(ReadOnlySpan<byte>, out ulong fingerprint, out int headerLength)`; `AvroMessageReader<T>` with an `ISchemaStore` (fingerprint → writer schema) to resolve automatically. Verified against `share/test/data/messageV1`.

### 4.9 JSON encoding
Implemented in M2 for the generic model: `GenericDatumJsonWriter` (over `Utf8JsonWriter`) and `GenericDatumJsonReader` (over `JsonDocument`, so record fields may come in any order). Union values other than `null` are wrapped as `{"fullName": value}`; `bytes`/`fixed` are strings whose code points 0–255 are the bytes; NaN and infinities are the strings `"NaN"`, `"Infinity"`, `"-Infinity"`, as Json.NET (and so Apache.Avro C#) writes them. A missing record field takes its default. Both directions are checked against Apache.Avro's `JsonEncoder`/`JsonDecoder` with random schemas and data; two Apache bugs are excluded from those tests (its grammar generator overflows the stack on recursive records, and its encoder writes nothing when a schema contains an empty record).

Same `AvroSerializer<T>` abstraction via an `IAvroEncoder` shape? **No** — keeping `AvroWriter` a ref struct with no virtual dispatch is the point; generated types get their own JSON path later (not part of M2.5, whose exit criteria are binary only).

---

### 4.10 Async

**Principle: async I/O, synchronous decoding.** `AvroReader` and `AvroWriter` are ref structs and cannot be held across an `await`. Async APIs therefore work a buffer or block at a time: read asynchronously (`PipeReader.ReadAsync`, `Stream.ReadAtLeastAsync` on net8+), decode that buffer synchronously from its `ReadOnlySequence<byte>` with the allocation-free reader, then yield the results. This is the same design as `System.Text.Json`'s async APIs; making each field read async would be far slower.

**Conventions, applied to every async API:**
- `ValueTask`/`ValueTask<T>` rather than `Task`; `CancellationToken` as the last parameter of every async method; `IAsyncEnumerable<T>` with `[EnumeratorCancellation]`; `IAsyncDisposable` so types work with `await using`.
- `ConfigureAwait(false)` throughout (already enforced by the analyzers); no sync-over-async anywhere in the library.
- netstandard2.0/2.1 get `IAsyncEnumerable<T>`/`IAsyncDisposable` from `Microsoft.Bcl.AsyncInterfaces`, added back when the first API uses it.

**Where each piece lands:**

| Milestone | Async API |
|---|---|
| M1 (done) | `AvroSchema.ParseAsync(Stream, CancellationToken)` → `ValueTask<AvroSchema>` |
| M2 / M5 | Reading a stream of datums without a container (a socket, a file of concatenated single-object messages): `ReadAllAsync(PipeReader or Stream, ct)` → `IAsyncEnumerable<T>`; writing datums to a `PipeWriter` with backpressure via `FlushAsync` |
| M5 | Container files: `AvroFileReader<T>.OpenAsync(Stream or PipeReader, ct)`; `await foreach (var record in reader.ReadAllAsync(ct))`; block-level `ReadBlockAsync` for parallel decoding; `AvroFileWriter<T>` with `WriteAsync`/`FlushAsync` and `await using` |
| M5 | Pipelined container reading: I/O, decompression and decoding overlap through `System.Threading.Channels`, with one block in flight per stage and bounded memory |
| v1.x (shipped in 1.0, in `AvroSharp.Confluent`) | Confluent: async Schema Registry lookups (cached) and Kafka serializers |

**Tests:** async paths are tested through the public APIs with streams that return data in small, uneven chunks, and with cancellation at every await point.

### 4.11 Field-run fusion (future)

Generated serializers (M2.5, M4) and the compiled generic plans know a record's field layout in advance, so they can look ahead for runs of consecutive fields and process each run as one block instead of field by field:

- **Consecutive `int`/`long` fields** (for example six counters in a row): decode them together with the bulk varint reader used for arrays (`AvroReader.ReadLongs`/`ReadInts`), whose vector check decodes a run of one-byte values at once and falls back to scalar decoding for larger values; encode them together the same way.
- **Consecutive `float`/`double` fields:** fixed-width little-endian values, so the whole run is one bounds check and one 8·N-byte copy (`MemoryMarshal` on little-endian hardware). This should help on every CPU, with or without SIMD.
- **Other fixed-width runs** (`boolean`, `fixed`, `float`, `double` in any mix): one bounds check for the whole run instead of one per field.

Limits: a union (including a nullable field) ends a run, because the branch is only known at read time; the varint gain depends on the data (small values benefit, 7-8-byte timestamps little). Like every SIMD or bulk path (Section 11, SIMD rules), a fused run ships only where local benchmarks show it beating per-field code, and it is tested against the per-field path for identical results.

Planned for: the M2.5 schema-file generator (emitted code), then the generic plan (a fused node) as a later optimization.

## 5. Performance plan (release gate)

### 5.1 Apache.Avro hot paths and how each is beaten

| # | Apache.Avro cost **[src]** | AvroSharp design |
|---|---|---|
| 1 | `BinaryEncoder.WriteString`: `Encoding.UTF8.GetBytes(string)` allocates a `byte[]` per string; `WriteBytes` then copies to `Stream` | `GetByteCount` + encode directly into the `IBufferWriter` window; zero allocation |
| 2 | `WriteLong`/`WriteDouble` write byte-by-byte to `Stream` (virtual call per byte) | Span stores; varint sized with `LeadingZeroCount`, written in one span write |
| 3 | `BinaryDecoder.ReadLong` calls `stream.ReadByte()` per byte; `ReadBytes` allocates `new byte[]` per call | `ReadOnlySpan<byte>` indexing; `ReadBytesSpan` returns a slice; `ReadString` decodes from span |
| 4 | `DefaultReader.Read` re-dispatches on `writerSchema.Tag` and re-resolves writer/reader per value | Resolution computed once into `ResolvedSchema`; typed nodes; generator emits straight-line code |
| 5 | `PreresolvingDatumReader.ReadItem` returns `object` (boxing every int/long/double/bool) | Generic path uses `AvroValue` struct; typed path is unboxed by construction |
| 6 | `GenericRecord` = `object[]`; `Add(name, value)` does name lookup | `AvroValue[]`, `FrozenDictionary` name→pos, position-based generated access |
| 7 | `ReflectDefaultReader` → `PropertyInfo.GetValue/SetValue` per field | Source-gen direct member access; runtime tier compiles accessors |
| 8 | `DataFileReader`: `byte[]` per block + `Decompress` returns `byte[]` + `MemoryStream` wrapper; `DataFileWriter` `MemoryStream` pair; `DeflateCodec.Decompress` creates 2 `MemoryStream`s | ArrayPool-backed `PooledBufferWriter` reused across blocks; codecs write into `IBufferWriter<byte>`; no `MemoryStream`; async I/O via `PipeReader` |
| 9 | No async: threads block on `Stream.Read` | `ValueTask`-based async with pipelines; sync path still zero-copy |
| 10 | Schema parse via Newtonsoft `JToken` DOM | `Utf8JsonReader` single pass |

### 5.2 Benchmark matrix (`bench/AvroSharp.Benchmarks`, BenchmarkDotNet, `[MemoryDiagnoser]`, `[ShortRunJob]` locally, full job in CI)

Datasets (all with fixed seeds, 1 000 records each unless stated):
- **P** primitives-heavy: 12 fields (int/long/float/double/bool).
- **S** string-heavy: 8 string fields, 10–200 chars, mixed ASCII/CJK.
- **N** nested: record → array<record> (avg 20) → map<string,record> (avg 5) → union [null, string, long].
- **L** logical: decimal(18,4) bytes, uuid, date, time-micros, timestamp-millis/micros/nanos, local-timestamp-micros, duration.
- **E** evolution: writer `N` vs reader with 2 fields removed, 2 added with defaults, 1 int→long promotion, reordered fields, union branch reorder.

Scenarios × implementations (AvroSharp-Gen, AvroSharp-Dynamic, AvroSharp-Generic, Apache.Avro Specific, Apache.Avro Generic, Apache.Avro Reflect, Chr.Avro):
1. Serialize to `byte[]`/`IBufferWriter` — P, S, N, L (typed and generic).
2. Deserialize from `byte[]` — P, S, N, L (typed and generic).
3. Resolution — E (typed and generic).
4. Container write 100 k records — N, codecs null/deflate/snappy/zstd.
5. Container read 100 k records — N, same codecs (sync `Stream` and async `PipeReader`).
6. Schema parse — small (P), large (a 300-field, 40-nested-type schema), canonical form + CRC-64.
7. Single-object encode/decode — P.

### 5.3 Pass criteria **[goal]**
- **Gate (hard, per scenario)**: mean time ≤ Apache.Avro's best-performing equivalent (Specific for typed, Generic for generic, its Snappy/Zstd satellite codecs for codecs) **and** allocated bytes/op ≤ Apache.Avro's. Any single scenario failing fails the gate.
- **Target margins** (goals to design toward, not claims): typed serialize/deserialize 3–6× faster with ≥90 % fewer allocations (P, S), 2–4× (N, L); generic 1.5–3×; container read/write 1.5–3× (codec-bound scenarios dominated by compressor, so margins are smaller); schema parse 2× with ≥80 % fewer allocations; resolution ≥3× (Apache re-resolves per value).
- Chr.Avro is a secondary reference: report, do not gate (archived; no container files to compare).
- The one exception to the gate, single-value writes on Zen+ CPUs, is in Section 11 (SIMD rules, #168).

### 5.4 Enforcement (local runs only)
- **Benchmarks never run in CI, on a schedule, or automatically.** They run locally, on request, on an otherwise idle machine. Every PR description recommends a run, with the exact command.
- **Comparative gate**: `dotnet run -c Release --project bench/AvroSharp.Benchmarks -f net10.0 -- --filter '*' --runtimes net8.0 net9.0 net10.0 --memory --gate`. With `--gate`, the process exits non-zero unless, in every group (class, category, parameters, runtime), each `AvroSharp_*` benchmark is faster than the Apache.Avro baseline and allocates no more. Chr.Avro benchmarks are reported but not gated.
- *Dropped:* the proposal's nightly full run on `main` and `ShortRunJob` gate on PRs (and the "full job in CI" in §5.2's heading); see the first point.
- Benchmarks run against pinned Apache.Avro 1.12.2 and Chr.Avro 10.13.1 in `Directory.Packages.props`.

---

## 6. Code generation

### 6.1 One engine, three front-ends
`AvroSharp.CodeGen` (netstandard2.0, no Roslyn dependency, deterministic output):
- Input: `AvroSchema`/`AvroProtocol` (+ later IDL via `AvroSharp.Idl`).
- Stage 1: **model** — `CsModel` (namespaces, types: `CsRecord`, `CsEnum`, `CsFixed`, `CsUnion`, members, attributes, mapped CLR types), naming conventions applied, collisions resolved (`Type` vs `Type_`, keyword escaping), reserved-name/keyword handling, dependency ordering.
- Stage 2: **emitter** — text via an indented writer; emits (a) types, (b) `static AvroSchema Schema` parsed once from the JSON, which is embedded as `public const string SchemaJson` on every target (a UTF-8 literal on net8+ was smaller, but made `SchemaJson` a property and put the schema in the source twice), (c) optional serializer/deserializer/resolution-plan partials (same code the attribute generator emits).
- `CodeGenOptions`: `TypeKind` (record | class | struct-for-fixed), `RecordsAreSealed`, `UseRequired`, `UseInit`, `Nullable`, `CollectionType` (`List<T>` | `T[]` | `ImmutableArray<T>` | `IReadOnlyList<T>`), `MapType` (`Dictionary` | `IReadOnlyDictionary`), `NamingConvention` (PascalCase properties, preserve enum symbols with `[AvroSymbol("...")]`), `NamespaceMap` (Avro ns → C# ns), `TypeOverrides` (schema fullname → CLR type), `GenerateSerializers`, `GenerateSchemaProperty`, `Accessibility`. *Shipped:* `CodeGenOptions` has `Namespace`, `NamespaceMap`, `LogicalTypes`, `PropertyNames`, `ApacheCompatible`, `NullableAnnotations`, `LanguageVersion` and `TargetHasDateOnly` (§8.1); the rest were not built, and records with `required`/`init` members are deferred (#15).

Front-ends:
1. **CLI** `AvroSharp.Tool`, the `avrosharp` command: a framework-dependent `dotnet tool` (like Apache.Avro's `avrogen`) on System.CommandLine 2.0, built for .NET 8 and .NET 10 with `RollForward=Major`, so it runs on .NET 8 or later; `dnx AvroSharp.Tool ...` runs it without installing it.
   - **Done** (#33, first part):
     - `gen <inputs>... -o <folder>`: inputs are files, or folders searched recursively for `*.avsc`, parsed together in any order (`SchemaFileSet`, shared with the source generator). Options map one to one onto `CodeGenOptions`: `-n/--namespace`, `-m/--namespace-map avro:csharp` (`NamespaceMap`: the longest Avro namespace that equals or prefixes a type's gets another C# namespace, as avrogen's `--namespace`; not with the Apache mode, since Apache.Avro finds types by the schema's full name), `--logical-types native|raw`, `--property-names pascal|avro`, `--apache-compatible`, `--no-nullable`, `--no-date-only`, `--language-version <n>` (7 or later). One `.g.cs` file per named type, in folders for its C# namespace (`com/example/Order.g.cs`; a type without an Avro namespace takes `--namespace`), or all in the output folder with `--flat` (`com.example.Order.g.cs`). Nothing is written if any schema is invalid or generation fails. Renamed properties are reported as `info AVROGEN005`.
     - `schema canonical <inputs>...` and `schema fingerprint <inputs>... [-a crc64|md5|sha256] [-f hex|base64|decimal]`: inputs as for `gen`, or `-` for one schema on standard input; `-r/--reference` adds files whose named types the inputs use. One input prints the result alone, several print `path: result` lines. `hex` and `base64` are the fingerprint's bytes as Avro writes them (CRC-64 little-endian); `decimal` is the signed CRC-64, as Java's `SchemaNormalization.parsingFingerprint64`, for `crc64` only.
     - `schema compat <writer> <reader>` (#165): whether data written with one schema can be read with the other, with every reason it can't; `--level` checks a new version against earlier ones at a registry's compatibility level (`backward`, `forward`, `full`, and their `-transitive` forms). Its exit codes add 3 (partially compatible) and 4 (incompatible).
     - **Output:** results and `info` lines on standard output; errors on standard error, as `path(line,column): error AVROGEN001: message` for schemas (the generator's diagnostic IDs), `error AVROGEN003: message` when generation fails, and `error: message` otherwise.
     - **Exit codes:** 0 success; 1 the command failed (an invalid schema, a missing file, output that could not be written); 2 the command line is not valid (an unknown command or option, a missing or invalid argument), with the error and a pointer to the command's `--help`.
   - **Not yet** (#33; the file commands are #172): `schema from-type`, `file dump|info|convert`, `.avpr` protocols, and packaging as a RID-specific hybrid tool (NativeAOT for `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64` plus a framework-dependent `any` fallback, which the .NET 10 SDK selects automatically), which would shorten `dnx` start-up.
2. **Build-time source generator** (`AvroSharp.Generators.SchemaFiles`): `<AdditionalFiles Include="Schemas/**/*.avsc" AvroNamespace="Acme.Events" AvroRecords="true" AvroNullable="true" AvroTypeOverride="..." />` plus `<CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="AvroNamespace" />` in the package `.props`. Incremental pipeline: `AdditionalTextsProvider` → parse (cached by content hash) → model → emit. Diagnostics for parse errors map to the `.avsc` file/line via `Utf8JsonReader` positions. No generated code checked in; `EmitCompilerGeneratedFiles` for debugging. *Shipped:* `SchemaFileGenerator` in `AvroSharp.Generators`, set by project-wide MSBuild properties (`AvroSharpNamespace` and the others in §8.1), not per-file metadata.
3. **Attribute-driven generator** (#31; designed in §6.5): `[AvroSerializable]` on a `partial` type (records/classes/structs) → emits `Schema`, `IAvroSerializable<T>` implementation, writer/reader, and a `ReaderPlan` switch for resolution; honors `[AvroName]`, `[AvroIgnore]`, `[AvroLogicalType]`, `[AvroUnion(typeof(A), typeof(B))]`, `[AvroFixed(16)]`, `[AvroDefault]`. Uses `ForAttributeWithMetadataName`, equatable model records, no `ISymbol` in the cache. Emits `[RequiresDynamicCode]`-free code, so consumers stay AOT-clean.

### 6.2 .NET 10 / C# 14 features — use or skip
- **`dnx` one-shot tool**: use (primary CLI UX). **RID-specific/AOT tool packages**: use.
- **File-based apps (`dotnet run gen.cs` with `#:package AvroSharp.CodeGen`)**: support by keeping `AvroSharp.CodeGen` a plain library with a one-call API (`CodeGenerator.Generate(schemaJson, options)`; shipped as [`CSharpCodeGenerator.Generate`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CSharpCodeGenerator.Generate.html), over parsed schemas); document a 10-line script. Don't build tooling around it.
- **MSBuild task**: skip — the source generator subsumes it, is incremental, and works in IDEs.
- **Interceptors**: skip for v1. They would let `AvroSerializer.Serialize(poco)` be intercepted with a generated call, but the `IAvroSerializable<TSelf>` static-abstract path gives the same zero-lookup result without the experimental feature flag.
- **C# 14 (extension members, `field` keyword, `nameof` on unbound generics, span conversions)**: use in library code where they simplify (e.g., `extension(AvroSchema)` for fluent helpers, `field` for lazy fingerprints); generated code stays at C# 12-compatible syntax when the consumer's `LangVersion` is lower (the generator checks `ParseOptions.LanguageVersion`).

### 6.3 Generated code shape (example)
```csharp
// <auto-generated/>  Acme.Events.User.g.cs
#nullable enable
namespace Acme.Events;

[global::AvroSharp.AvroSchemaSource("user.avsc")]
public sealed partial record User : global::AvroSharp.IAvroSerializable<User>
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }                         // union ["null","string"]
    public global::System.DateOnly BirthDate { get; init; }     // int + date
    public global::System.DateTimeOffset CreatedAt { get; init; } // long + timestamp-micros
    public global::System.Guid TenantId { get; init; }          // string + uuid
    public decimal Balance { get; init; }                       // bytes + decimal(18,4)
    public global::System.Collections.Generic.List<Address> Addresses { get; init; } = [];
    public Role Role { get; init; }                             // enum → C# enum
    public Contact Contact { get; init; }                       // union [Phone, Email] → generated DU

    public static global::AvroSharp.AvroSchema Schema => s_schema ??= global::AvroSharp.AvroSchema.Parse(SchemaJson);
    // + generated Write(ref AvroWriter, in User) / Read(ref AvroReader, ReaderPlan)
}

public abstract partial record Contact   // closed hierarchy DU shape for non-nullable unions
{
    private Contact() {}
    public sealed partial record PhoneCase(Phone Value) : Contact;
    public sealed partial record EmailCase(Email Value) : Contact;
    public TResult Match<TResult>(Func<Phone,TResult> phone, Func<Email,TResult> email);
}
```
Rules: `[null, T]` → `T?`; `[null, T1, T2]` → `Union2<T1,T2>?`-style generated DU; enums → C# enum with `[AvroSymbol]` for non-identifier symbols and `[AvroEnumDefault]`; fixed → `readonly partial struct` wrapping `byte[]`/inline array (`[InlineArray]` on net8+); `duration` → `AvroDuration` struct (months, days, millis); `big-decimal` → `BigInteger`-backed `AvroBigDecimal` struct (`decimal` is limited to 28–29 digits); `timestamp-nanos` → `DateTimeOffset` with precision loss note, or `long` when `PreserveNanos=true`; `Int128` only used for `decimal` logical types whose precision > 28 when the user opts into `DecimalType=Int128`. netstandard2.0 fallbacks: `DateOnly`→`DateTime`(date-only), `TimeOnly`→`TimeSpan`, `required`/`init` polyfilled by PolySharp attributes (`IsExternalInit`, `RequiredMemberAttribute`, `SetsRequiredMembers`) — legal on netstandard because they are compile-time only.

*Shipped (M2.5 status in Section 11):* the example above is the proposal. A record becomes a `partial class` with get/set properties, and a union with more than one non-null branch an `object?` property; generated union types (#14) and `required`/`init` members (#15) are deferred past 1.0 (§8.1). `duration`, `timestamp-nanos` and decimals wider than `decimal` keep their underlying type, so there are no `AvroDuration` or `AvroBigDecimal` types. The mapping is in [code-generation.md](code-generation.md#type-mapping).

### 6.4 Codegen tests
- **Snapshot tests** (Verify.Xunit v3 + `Verify.SourceGenerators`): one `.verified.cs` per schema fixture × option set; diffs reviewed in PRs.
- **Compile-and-roundtrip**: generated source is compiled in-test with Roslyn (`CSharpCompilation` + `Basic.Reference.Assemblies`) for net10 and netstandard2.0 reference sets, loaded via `AssemblyLoadContext`, then round-tripped through the real `AvroSerializer<T>`/`AvroFileWriter<T>` and cross-checked against Apache.Avro's `GenericDatumReader`.
- **Generator tests**: `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing` (xunit adapter) for diagnostics, incremental caching (`IncrementalStepRunReason.Cached` assertions), and AdditionalFiles metadata.
- The generator itself is also dog-fooded: `samples/` and the benchmark project use it, so the product path is what's measured.
- *Shipped:* the tests run on TUnit, without Verify: `AvroSharp.Generators.Tests` drives the generator with Roslyn and compares against its own `Snapshots/` files, and the compile-and-roundtrip tests are consumer projects built by the SDK (`AvroSharp.Generators.Consumer.Tests` and the others).

### 6.5 The attribute-driven generator (#31)

The `.avsc` generator starts from a schema and writes the C# types. This one starts from C# types the user wrote, and writes the schema and the serializers. Status: **implemented in the first version below (§6.5.5 lists where it is narrower than this design, for now).**

#### 6.5.1 Shape

```csharp
[AvroSerializable(Namespace = "acme.orders")]
public partial class Order
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public string? Note { get; set; }                      // ["null","string"], default null
    public List<OrderLine> Lines { get; set; } = [];
    public Status Status { get; set; }                     // a C# enum becomes an Avro enum
    public DateTimeOffset PlacedAt { get; set; }           // long, timestamp-micros

    [AvroDecimal(18, 2)] public decimal Total { get; set; }
    [AvroName("legacy_ref"), AvroAlias("ref")] public string? Reference { get; set; }
    [AvroIgnore] public decimal CachedTax { get; set; }
}
```

**What the generator adds to the `partial` type** is the same set of members that a type generated from a `.avsc` file has, so everything that takes generated types takes these as well:
- `Schema` and `SchemaJson`;
- `Write`, `Read`, and `Read(ref reader, writerSchema)`, with the same resolution plans (§4.6);
- `ToAvroBytes`, `FromAvroBytes`, `TryWriteAvroBytes` and `WriteAvroBytes`;
- `IAvroSerializable<TSelf>` on .NET 8+ with C# 11+, and `IAvroWritable` and `IAvroReadable`.

This covers `AvroSerializer`, `AvroFileReader.Open<T>`, `AvroMessageReader.Create<T>` and the registry readers.

**Packaging:**
- The attributes live in the `AvroSharp` package, in `AvroSharp.Serialization`, so that the types compile without the generator.
- The generator ships in the `AvroSharp.Generators` package, beside `SchemaFileGenerator`, so there's one package to add.

**One emitter:**
- The generator builds an `AvroSchema`, plus a binding of each field to its member, from the Roslyn symbols.
- `AvroSharp.CodeGen` then emits only the serializer part, using the user's member names and types.
- The `.avsc` path keeps declaring the types, as it does today.
- So both front-ends share the read and write code, the resolution plans and the benchmarks.

**What emitting serializers for existing types needs from `CSharpCodeGenerator`:** an entry point that skips the type and property declarations, takes the property name for each field, and checks that each member's type is one that `TypeMapper` would have chosen for the field's schema (§6.5.3).

**Incrementality** follows the analyzer cookbook:
- `ForAttributeWithMetadataName("AvroSharp.Serialization.AvroSerializableAttribute")`;
- an equatable model (names, types as strings, the JSON of the schema) with no `ISymbol` kept;
- one generated file per type.

#### 6.5.2 Attributes

| Attribute | Targets | Meaning |
|---|---|---|
| `[AvroSerializable]` | `partial` class, `partial` record class | Generate the schema and serializers. Properties: `Name` and `Namespace` (default: the type's name, and its C# namespace), `Doc`, `FieldNames` (see naming below). |
| `[AvroName("n")]` | property, field, enum member | The Avro name of the field or symbol, used as is (no naming policy). |
| `[AvroAlias("a")]`, repeatable | type, property, field | Aliases, for renaming (§4.6 resolution). |
| `[AvroDoc("…")]` | type, property, field, enum | The `doc` attribute. Without it, the XML `<summary>` is used when the compilation has documentation comments on. |
| `[AvroIgnore]` | property, field | Not part of the schema. |
| `[AvroDefault("json")]` | property, field | The field's default, as Avro JSON. Checked at compile time against the field's schema (AVROGEN106). A nullable member gets `"default": null` without it. |
| `[AvroDecimal(precision, scale)]` | `decimal` member | Required for `decimal`: Avro has no default precision. On `bytes`, or on `fixed` with `[AvroFixed]`. |
| `[AvroFixed(size)]` with `Name` | `byte[]` member | A `fixed` type instead of `bytes`. The `Name` defaults to the member's name; the size is checked when writing. |
| `[AvroLogicalType("timestamp-millis")]` | `DateTimeOffset`, `DateTime`, `TimeOnly`, `TimeSpan`, `DateOnly`, `long`, `int` or `string` member | Another logical type than the default for the member's type (§6.5.3). On a raw `long`, `int` or `string` it annotates the value, which the member keeps (`"avrosharp.logicalType": "raw"`). |
| `[AvroUnion(typeof(A), typeof(B), …)]` | property of type `object`, or of an abstract base class of the listed types | A union of the listed record types (and `null` when the member is nullable). Written by the value's runtime type, as `object?` unions are today. |
| `[AvroEnumDefault]` | an enum member | The enum's `default` symbol, for readers that meet an unknown symbol. |
| `[AvroFieldPosition(n)]` | property, field | The field's position (Avro's own `"order"` is the sort order), needed only when the fields are declared in more than one part of a partial type (see "Field order" below). |

The attributes leave out two things for now:
- avro-rs's `flatten`;
- its `with`, a schema for a type you don't own, which belongs to #119's converters.

**Naming:**
- Field names are the member's name as written by default, so what you write is what you get.
- `[AvroSerializable(FieldNames = AvroNaming.CamelCase)]` converts them, `PascalCase` to `pascalCase`. This is the inverse of the `.avsc` generator's default `PascalCase` property names, so a schema's `id` field and an `Id` property round-trip.
- An assembly-level `[assembly: AvroSerializableDefaults(FieldNames = AvroNaming.CamelCase)]` sets the default for every type; more assembly-wide defaults can join it.
- Enum symbols use the member names as written; `[AvroName]` renames them.
- The names are checked against the Avro name rules (AVROGEN104).

**Field order** is declaration order. Roslyn orders the members of a partial type across files by syntax tree, which the build doesn't fix. So when the serialized members span more than one `partial` declaration, every one needs `[AvroFieldPosition(n)]` (AVROGEN109). Inherited members come first, base class first.

**Members:**
- **Included:** public instance properties with a getter and a setter or `init`, and public fields.
- **Not included:** static members, indexers, and read-only properties.
- **`init`-only members:** a type with any of them gets no `IAvroReadable`, because `ReadFrom` would have to assign after construction.

**Construction:**
- **The constructor reading uses:** the generator adds a private constructor taking the existing reader marker (`AvroUninitialized`), which skips the property initializers, as generated records do.
- **What that needs from the type:** the type must not declare a primary constructor (AVROGEN108).
- **Positional records:** binding fields to constructor parameters is a later step (§6.5.5).

#### 6.5.3 C# types to Avro

| C# | Avro | Notes |
|---|---|---|
| `bool`, `int`, `long`, `float`, `double`, `string`, `byte[]` | `boolean`, `int`, `long`, `float`, `double`, `string`, `bytes` | |
| `T?` (value type), or a reference type annotated `?` | `["null", T]`, default `null` | With nullable annotations off, a reference type is not nullable unless it is `[AvroUnion]` or `[AvroDefault("null")]`. |
| a C# `enum` (any underlying type) | `enum` | Name and namespace from the enum type, or `[AvroName]` on the enum. Symbols in declaration order. Values are written by name, not by number. |
| an `[AvroSerializable]` class | `record`, by name after its first use | A class without the attribute is an error (AVROGEN111). |
| `List<T>` | `array` | `T[]`, `IList<T>` and `IReadOnlyList<T>` come later; they need the emitter's collection helpers widened. |
| `Dictionary<string, T>` | `map` | Other key types are an error (Avro maps have string keys). |
| `Guid` | `string`, `uuid` | `[AvroFixed(16)]` makes it `fixed(16)`, `uuid`. |
| `decimal` | `bytes`, `decimal(p, s)` | Needs `[AvroDecimal]` (AVROGEN103). |
| `DateOnly` | `int`, `date` | |
| `TimeOnly` | `long`, `time-micros` | `[AvroLogicalType("time-millis")]` for `int`, `time-millis`. |
| `DateTimeOffset` | `long`, `timestamp-micros` | Micros keep .NET's precision best among the logical types that most readers support. `timestamp-millis` by attribute. |
| `DateTime` | **error unless annotated** (AVROGEN114) | A `DateTime`'s `Kind` makes UTC versus local ambiguous. `[AvroLogicalType("local-timestamp-micros")]` (or `-millis`) for local time; a UTC timestamp is a `DateTimeOffset`. On targets without `DateOnly`, `("date")` too. |
| `TimeSpan` | only with `[AvroLogicalType("time-micros")]`, and only on targets without `TimeOnly` | It means a time of day there, as the `.avsc` generator's fallback. Elsewhere use `TimeOnly`. |
| `short`, `byte`, `char`, unsigned types, `Half`, `Int128`, `BigInteger` | error (AVROGEN102) | Avro has no such types. Widening them silently would make the read value's range a surprise. A later step may allow them with an explicit `[AvroType("int")]`. |

Generic types, and types nested in other types, are errors (AVROGEN107); enums may be nested. A C# enum's values must be 0, 1, 2 and so on, in declaration order, since Avro writes enums by position (AVROGEN116). A schema name defined by two types, or an Avro type reached through two different C# types, is an error (AVROGEN113).

#### 6.5.4 Lookup by type, and primitives (from #31's comments)

**Who needs this:** integrations (#185 and the others) need a type's schema and its read and write functions without reflection, on every target.
- `static abstract` interface members cover generic code on .NET 8+.
- They don't cover netstandard2.0, or a lookup by `Type` (Lambda Powertools' `Deserialize(byte[], Type)`).

**The API (additive):**
- `AvroTypeInfo<T>`, which holds:
  - `Schema`;
  - `Write`, an `AvroWriteAction<T>`;
  - `Read`, an `AvroReadFunc<T>`;
  - `ReadFor(AvroSchema writerSchema)`, an `AvroReadFunc<T>`.
- `AvroTypes.Get<T>()` and `AvroTypes.TryGet<T>(out AvroTypeInfo<T>)`.
- `AvroTypes.TryGet(Type, out AvroTypeInfo)`, a non-generic view with boxed delegates.

**How types get in:**
- Every generated type, from either generator, has a static `AvroTypeInfo` property.
- On .NET 5 and later with C# 9 or later, a `[ModuleInitializer]` in each generated type registers it when its assembly loads.
- Elsewhere (netstandard2.0, .NET Framework), code calls `AvroTypes.Register(Order.AvroTypeInfo)` once. Emitting a polyfill of the attribute would collide with other generators' polyfills, such as PolySharp's.
- The library registers the primitives that Confluent's serializers support: `int`, `long`, `float`, `double`, `bool`, `string` and `byte[]`. Another primitive writer schema is read with the resolving reader's promotions.
- Nothing uses reflection, so lookup is AOT- and trim-safe.

#31 asked for an `AvroSerializer<T>` API. `AvroSerializer.Serialize<T>` and `Deserialize<T>` already exist for `IAvroSerializable<T>`, but only on .NET 8+. With `AvroTypes`, they can also have overloads for netstandard2.0 and for the primitives (not in 1.0, which keeps `AvroSerializer` on .NET 8+ for `IAvroSerializable<T>` types).

#### 6.5.5 Scope of the first version, and what comes later

**First version:**
- classes and record classes;
- the attributes above;
- the type mapping above;
- `[AvroUnion]` of records;
- `AvroTypes`;
- diagnostics AVROGEN101 to AVROGEN118.

**Where the first version is narrower than the design above:**
- **`init`-only members** are an error (AVROGEN115). The readers assign properties after construction, and `IAvroReadable` refills an instance.
- **Types nested in other types** are an error (AVROGEN107). Enums may be nested.
- **Enum values** must be 0, 1, 2 and so on (AVROGEN116).
- **`[AvroUnion]`** goes on `object` members only, not on a base class.
- **Constructors:** a type that declares no constructors gets the public parameterless one back. Declaring the readers' private constructor would otherwise remove the implicit one.
- **Member names** that the generator adds (`Schema`, `Write`, `Read` and the others) are an error on the type (AVROGEN117).

**Later, each additive:**
- positional records, `init`-only members and constructor binding;
- nested types;
- enums with any values, mapped by name;
- `[AvroUnion]` on a base class;
- `T[]` and collection interfaces;
- structs;
- narrower integer types with `[AvroType]`;
- class hierarchies as unions in avro-rs's other representations (overlaps with #14);
- `with`/converters (#119);
- `avrosharp schema from-type` (#33).

**Diagnostics:**
- AVROGEN101: not `partial`.
- AVROGEN102: a member type that isn't supported (the message lists what is).
- AVROGEN103: `decimal` without `[AvroDecimal]`.
- AVROGEN104: an invalid Avro name.
- AVROGEN105: two members with the same Avro name.
- AVROGEN106: an invalid `[AvroDefault]`.
- AVROGEN107: a generic type, or one nested in another type.
- AVROGEN108: a primary constructor.
- AVROGEN109: field order is ambiguous across partial declarations.
- AVROGEN110: `[AvroUnion]` doesn't fit the member's type.
- AVROGEN111: a referenced class is not `[AvroSerializable]`.
- AVROGEN112: an attribute on a member type it doesn't apply to.
- AVROGEN113: a schema name collision.
- AVROGEN114: `DateTime` without a logical type.
- AVROGEN115: an `init`-only member.
- AVROGEN116: enum values other than 0, 1, 2 and so on.
- AVROGEN117: a member with the name of a member the generator adds.
- AVROGEN118: code generation failed (an internal error, with the message).

All are errors. AVROGEN112 was a warning in the design, but a misapplied attribute silently changes the schema, and an error can become a warning later without breaking builds, not the other way round.

**Exit criteria** (from #31):
- Typed benchmarks beat Apache's specific and reflect paths. They share the `.avsc` path's emitter, so this is checked by adding attribute-driven twins of `GenericRecordBenchmarks`' generated types.
- No trim or AOT warnings; the AOT smoke test gets an attribute-driven type.
- Snapshot tests of the generated code and of every diagnostic.
- A round trip of every mapped type against the same schema written as `.avsc`: the same canonical form, and byte-identical data.

**Decided (2026-10-01):**
1. **Field names are as written by default.** This is Apache's rule, as Java's `ReflectData` and Apache C#'s `[AvroField]` matching use member names unchanged. camelCase is opt-in, per type or per assembly. The docs say that a project whose schemas are read by Java or other languages should set camelCase for the assembly, since their fields are camelCase by convention.
2. **`DateTime` is an error unless annotated** (AVROGEN114).
3. **`AvroTypes` ships with #31.**

---

## 7. Multi-targeting strategy

- TFMs: `net10.0;net9.0;net8.0;netstandard2.1;netstandard2.0` for `AvroSharp` and codecs; `netstandard2.0` only for generators/CodeGen; `net10.0` for the tool. *Shipped:* `AvroSharp.CodeGen` is `netstandard2.0;net8.0` and the tool `net8.0;net10.0` (§6.1). The add-ons are `net10.0;net9.0;net8.0;netstandard2.0`, except `AvroSharp.KafkaFlow` (`net10.0;net9.0;net8.0`). .NET 11 targets follow its release (#222).
- `LangVersion=preview`/`14` everywhere; **PolySharp 1.16** (supports C# 14 **[docs]**) for language-feature polyfills (`IsExternalInit`, `required`, `[CallerArgumentExpression]`, `[SkipLocalsInit]`, `[UnscopedRef]`, nullable attributes, `[StackTraceHidden]`, `[ModuleInitializer]`).
- BCL polyfills for netstandard: `System.Memory` (Span, `BinaryPrimitives`, `ArrayPool`, `IBufferWriter`, `ReadOnlySequence`), `System.Buffers`, `System.Text.Json` (netstandard2.0-compatible), `System.IO.Pipelines`, `Microsoft.Bcl.AsyncInterfaces` (`IAsyncEnumerable`, `IAsyncDisposable`), `System.Collections.Immutable`, `Microsoft.Bcl.HashCode`, `System.Threading.Tasks.Extensions` (`ValueTask`), `System.Runtime.CompilerServices.Unsafe`. Consider `Polyfill` (SimonCropp) instead of hand-written shims for `Encoding.GetString(ReadOnlySpan<byte>)`, `Stream.ReadAsync(Memory<byte>)`, `Random.Shared` etc. — recommended: **Polyfill** as an internal source-only package on netstandard TFMs only. *Shipped:* Polyfill covers both the language features and the BCL shims, in every shipped project, so PolySharp isn't used. The core's netstandard package dependencies are the four in §3.
- `#if NET8_0_OR_GREATER` islands limited to: `FrozenDictionary`, `SearchValues`, `Utf8.ToUtf16`, `BitOperations` (in netstandard2.1 too via System.Numerics? — no; polyfilled), `[InlineArray]`, `IUtf8SpanFormattable`, `Vector128` in varint batch decode (optional), `RandomNumberGenerator.Fill`, `DateOnly/TimeOnly` (net6+, so use `NET6_0_OR_GREATER`... but our lowest net TFM is 8 so `NET` suffices), static abstract interface members (`IAvroSerializable<TSelf>.Schema` is static-abstract on net8+ and instance-less static in generated code on netstandard).
- **Degradations on netstandard2.0**: no static abstracts (use a `AvroSerializerRegistry` lookup, one dictionary hit per call); `DateOnly/TimeOnly` map to `DateTime/TimeSpan`; expression-tree tier works on .NET Framework via JIT but ref-struct lambdas require the `AvroBufferWriter` façade; no `FrozenDictionary` (plain `Dictionary`); slightly slower UTF-8 transcoding (no `Utf8.ToUtf16`); no `IsAotCompatible` semantics (n/a). Everything else, including async pipelines and container files, works.
- **Portability layer rule: the net8.0/net9.0/net10.0 path is the fast path, and netstandard support must never slow it down.**
  - Compat code for netstandard2.0/2.1 is compiled only there (`#if !NET8_0_OR_GREATER`, or a source-only polyfill that emits nothing when the API exists). On net8+ the code calls the BCL API directly, with no wrapper and no extra call.
  - If a shared helper can't be avoided, it is marked `[MethodImpl(MethodImplOptions.AggressiveInlining)]` and its net8+ body is a single BCL call, so the JIT produces the same code as calling the API directly.
  - Hot paths get no runtime indirection for portability (no interfaces, delegates or virtual calls to pick an implementation); the choice is made at compile time.
  - Newer targets may add `#if NET9_0_OR_GREATER` / `NET10_0_OR_GREATER` branches when they bring faster APIs.
  - Benchmarks run on net8.0, net9.0 and net10.0, and the performance gate applies to each. netstandard (including .NET Framework) must be correct and reasonable, but is not optimized at the cost of the modern targets.
- `IsAotCompatible=true` (net8+), `EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer` on all TFMs where supported; `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` only on the dynamic and reflection tiers. (Those tiers were dropped. The only such attributes shipped are `[RequiresUnreferencedCode]` on `AvroSharp.KafkaFlow`'s lookup of message types by name, which has an overload that takes the types.)

---

## 8. Public API sketch

The sketch below is updated to the 1.0 API, checked against `PublicAPI.Shipped.txt`; the [API reference](https://avrosharp.github.io/AvroSharp/docs/api/index.html) lists every type and member. The proposal's version also had `AvroSerializer.Get` and `CreateDynamic`, `SchemaBuilder` and a public `ResolvedSchema`, which were not built (see the decisions at the top); `[AvroSerializable]` was built by the attribute generator (§6.5), and lookup by type is `AvroTypes` (§6.5.4). [`AvroSerializer`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.html) and the `Create<T>`/`Open<T>` overloads without a delegate need .NET 8 or later; the generated `ToAvroBytes` and `FromAvroBytes` work on every target.

```csharp
// Serialize a generated type (the source generator, from Order.avsc)
var buffer = new ArrayBufferWriter<byte>();
AvroSerializer.Serialize(buffer, order);                 // uses Order.Schema; no lookup
byte[] bytes = AvroSerializer.Serialize(order);
Order back = AvroSerializer.Deserialize<Order>(bytes);   // writer == reader schema
Order back2 = AvroSerializer.Deserialize<Order>(bytes, oldSchema); // resolution
byte[] same = order.ToAvroBytes();                       // every target, netstandard2.0 included
Order back3 = Order.FromAvroBytes(bytes, oldSchema);

// The generated reader and writer, over AvroReader/AvroWriter
Order.Write(ref writer, order); Order read = Order.Read(ref reader);

// Container file, async
await using var fw = AvroFileWriter.Create<Order>(stream, new AvroFileWriterOptions { Codec = ZstandardCodec.Default });
await fw.WriteAsync(order, ct); await fw.FlushAsync(ct);

await using var fr = await AvroFileReader.OpenAsync<Order>(stream, cancellationToken: ct);
await foreach (var o in fr.ReadAllAsync(ct)) { ... }
AvroSchema writerSchema = fr.WriterSchema; IReadOnlyDictionary<string, ReadOnlyMemory<byte>> meta = fr.Metadata;

// Generic records
var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"User","fields":[{"name":"id","type":"long"}]}""");
var rec = new GenericRecord(schema) { ["id"] = 42L };   // AvroValue: no boxing
byte[] gbytes = GenericDatumWriter.Create(schema).WriteToArray(rec);
GenericRecord gback = GenericDatumReader.Create(oldSchema, schema).Read(gbytes).AsRecord(); // resolution
using var gfr = AvroFileReader.OpenGeneric(stream);     // AvroFileReader<AvroValue>

// Schema APIs
string canonical = schema.CanonicalForm;
long fp = schema.Fingerprint64;                         // CRC-64-AVRO
byte[] sha = SchemaFingerprint.Sha256(schema);

// Single-object encoding
AvroMessage.Write(buffer, order);
var messages = AvroMessageReader.Create<Order>(new AvroSchemaStore(Order.Schema)); // IAvroSchemaResolver: fingerprint -> schema
Order m = messages.Read(message);
```

### 8.1 Compatibility policy and the pre-1.0 API review (#134)

**Generated code and the runtime.** The source generator and `avrosharp gen` emit calls into a support surface. Code from `avrosharp gen` is often checked in and not regenerated, so from 1.0 that surface is a compatibility contract:
- **Where it lives:** `AvroSharp.Serialization.Generated`:
  - `AvroGeneratedCode`, `AvroRecordPlan`, `AvroPlanCache` and `AvroConversion`;
  - `AvroUninitialized`;
  - `IAvroValueSerializer<T>` and the primitive serializers (`AvroIntSerializer` and the others).
- **Visibility:** every one of these types is `[EditorBrowsable(Never)]`. They exist for generated code, not for users.
- **Compatibility:**
  - Code generated by version x.y compiles and runs against runtime x.z for any z ≥ y. Within a major version, the surface only grows.
  - A member leaves only after a release that marks it `[Obsolete]`, and only in a major version.
  - A generator of version x.y may need runtime x.y or later. The source generator and the runtime ship together. Each generated type's `[GeneratedCode("AvroSharp.CodeGen", version)]` names its generator's version, the lowest runtime version it needs.
- **Before 1.0:** the policy starts at 1.0. The 0.x releases could still change the surface; the CHANGELOG lists each change and says to regenerate. Since 1.0.0-rc.1 the surface is frozen with the rest of the core API, and from 1.0.0 it follows semantic versioning (see the freeze below).
- **Kept:** `AvroGeneratedCode.SerializeToArray<T>` looks like a duplicate of `AvroSerializer.Serialize<T>`, but it stays. Generated `ToAvroBytes()` calls it on every target, and [`AvroSerializer.Serialize<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.Serialize.html) needs static abstract members, so it exists only on .NET 8 and later.

**Decisions of the review.** Each item of #134, changed or kept:
- **The support surface:** moved to `AvroSharp.Serialization.Generated`, as above.
  - [`AvroWriteAction<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroWriteAction-1.html) and [`AvroReadFunc<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroReadFunc-1.html) stay in [`AvroSharp.Serialization`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.html): the file, stream and message APIs take them.
  - Removed, because generated code no longer calls them: `GetRecordPlan(writer, reader)` without the cache, `PutTypeMismatch(object?, string, string)`, and `AvroRecordPlan.Target(int)`/`Conversion(int)`.
- **"Codec" means block compression only:**
  - `IAvroCodec<T>` is now `IAvroValueSerializer<T>`, and the primitive codecs are `Avro…Serializer`.
  - The generated nested struct is now `ValueSerializer`. As `AvroCodec`, it hid [`AvroSharp.Containers.AvroCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.html) inside users' partial classes.
- **Schema lookup:** one noun for each role.
  - The interfaces are resolvers: [`IAvroSchemaResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaResolver.html) (by fingerprint, for single-object messages) and [`IAvroSchemaIdResolver`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.IAvroSchemaIdResolver.html) (by registry ID).
  - The `…Store` classes, [`AvroSchemaStore`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaStore.html) and [`AvroSchemaIdStore`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaIdStore.html), are their in-memory implementations.
  - Both interfaces have a synchronous and an asynchronous lookup. On netstandard2.0, which has no default interface members, adding one after 1.0 would break every implementer.
  - [`AvroMessageReader<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessageReader-1.html) has [`ReadAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessageReader-1.ReadAsync.html), like [`AvroRegistryMessageReader<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessageReader-1.html). It fetches an unknown fingerprint once; a failed or cancelled fetch isn't cached.
- **`AvroSerializer`:** [`Serialize(output, value)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.Serialize.html) and [`TrySerialize(destination, value, out bytesWritten)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.TrySerialize.html) take the output first, as every other write API does. [`Deserialize<T>(in ReadOnlySequence<byte>, AvroSchema writerSchema)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.AvroSerializer.Deserialize.html) completes the read overloads.
- **Names that pair:**
  - `GenericDatumReader.Schema` and `GenericDatumJsonReader.Schema` are now [`WriterSchema`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.WriterSchema.html), as everywhere else. (The writers' `Schema` is the only schema they have, so it stays.)
  - [`AvroFileReader<T>.Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.Codec.html) is now the `AvroCodec`, as [`AvroFileWriterOptions.Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.Codec.html) is; [`Codec.Name`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.Name.html) is the header's `avro.codec`.
  - [`AvroFileWriterOptions.Metadata`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.Metadata.html) holds `ReadOnlyMemory<byte>` values, as [`AvroFileReader<T>.Metadata`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.Metadata.html) does.
  - [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html)'s factories pair with its accessors: [`FromBytes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.FromBytes.html), [`FromRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.FromRecord.html) and [`FromFixed`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.FromFixed.html) (were `FromByteArray`, `FromGenericRecord` and `FromGenericFixed`). Primitives take their .NET names (`FromInt32`/`AsInt32`), and the other kinds their Avro names; the struct's docs state the rule. CA2225 wants the .NET names for the implicit operators' alternates, and is suppressed there.
  - `EnumSchema.Default` is now [`DefaultSymbol`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.EnumSchema.DefaultSymbol.html), like the constructor parameter; `Default` read like a static.
  - `AvroReader.Skip(long)` is now [`SkipRaw`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.SkipRaw.html), so it doesn't read as a sibling of [`SkipBytes()`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.SkipBytes.html), which skips an Avro `bytes` value.
- **Exceptions:** [`AvroException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroException.html), [`AvroDataException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroDataException.html) (malformed data) and [`AvroSchemaException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroSchemaException.html) (invalid schemas) are all in the [`AvroSharp`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.html) namespace. Code in any `AvroSharp.*` namespace sees them without a `using`.
- **Schema equality:** [`AvroSchema.HasSameCanonicalForm(AvroSchema)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchema.HasSameCanonicalForm.html) is public: it compares encodings, and remembers the last match. `Equals` stays reference equality by design, because schemas with the same canonical form can differ in docs, aliases, defaults and properties; `AvroSchema`'s docs say so. `AvroGeneratedCode.IsSameSchema` is gone, and generated code calls `HasSameCanonicalForm`.
- **`DeflateCodec`:** the built-in codec is public, with the Codecs package's shape: [`DeflateCodec.Default`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.Default.html), a [`constructor`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.-ctor.html) that takes the `CompressionLevel`, and [`Level`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.DeflateCodec.Level.html). [`AvroCodec.Deflate`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.Deflate.html) stays as the short spelling, next to [`AvroCodec.Null`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodec.Null.html). `AvroCodec.CreateDeflate` is gone.
- **`RecordField`:** a field keeps [`Record`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.RecordField.Record.html) and [`Position`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.RecordField.Position.html), which the readers and the generator use. A record given a field that already belongs to another one attaches a copy instead of throwing, so `new RecordSchema(name, other.Fields.Append(field))` works. Parsed schemas always create fresh fields, so they never copy.
- **Generator options:** one name per setting across `CodeGenOptions`, MSBuild and the CLI.
  - [`CodeGenOptions.Namespace`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.Namespace.html), [`NamespaceMap`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.NamespaceMap.html), [`LogicalTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.LogicalTypes.html), [`PropertyNames`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.PropertyNames.html) and [`ApacheCompatible`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.ApacheCompatible.html); were `DefaultNamespace`, `NamespaceMapping` and `PropertyNaming`.
  - MSBuild `AvroSharpNamespace`, `AvroSharpNamespaceMap` (new), `AvroSharpLogicalTypes`, `AvroSharpPropertyNames` and `AvroSharpApacheCompatible`.
  - CLI `--namespace`, `--namespace-map`, `--logical-types`, `--property-names` and `--apache-compatible`.
  - The values are the same everywhere (`native|raw`, `pascal|avro`, `true|false`), case-insensitive in MSBuild. An MSBuild value the generator doesn't recognize is warning AVROGEN006, and the default is used.
  - [`NullableAnnotations`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.NullableAnnotations.html) with a [`LanguageVersion`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.LanguageVersion.html) below 8, or a version below 7, is an `ArgumentException`. The CLI derives it: `--language-version 7` implies `--no-nullable`. The source generator takes both from the project.
- **Small ones:**
  - [`SchemaFingerprint.Crc64AvroEmpty`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.SchemaFingerprint.Crc64AvroEmpty.html) is a `long`, like every fingerprint. The polynomial isn't public.
  - [`AvroFileReader<T>.TryGetMetadataString`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.TryGetMetadataString.html) replaces `GetMetadataString`.
  - [`ConfluentSchemaIdHeader.Encode`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.ConfluentSchemaIdHeader.Encode.html) takes the [`AvroSchemaId`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroSchemaId.html) that [`TryDecode`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.ConfluentSchemaIdHeader.TryDecode.html) gives, and rejects a numeric ID.
  - [`GenericRecord.TryGetValue(int position, …)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.TryGetValue.html) names its parameter as the indexer does.
  - **Limits:** each options class has public default constants ([`GenericDatumReaderOptions.DefaultMaxDepth`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReaderOptions.DefaultMaxDepth.html) and [`DefaultMaxZeroSizeItems`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReaderOptions.DefaultMaxZeroSizeItems.html), [`GenericDatumWriterOptions.DefaultMaxDepth`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriterOptions.DefaultMaxDepth.html), [`AvroSchemaParseOptions.DefaultMaxDepth`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaParseOptions.DefaultMaxDepth.html)), in place of the literal 128 in four places. The limits reject out-of-range values when set: a depth of at least 1, and zero or more items.
  - **Missing overloads, added:** [`GenericDatumReader.Read(in ReadOnlySequence<byte>)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.Read.html), [`AvroMessage.Write(output, in AvroValue, GenericDatumWriter)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroMessage.Write.html) and [`AvroRegistryMessage.Write(output, framing, id, in AvroValue, GenericDatumWriter)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Messages.AvroRegistryMessage.Write.html). The `ToArray` overloads now call them.

**The freeze (#73).** The API of the four core library packages is declared shipped, and 1.0.0-rc.1 was released with it. The add-on packages (§3) were first published in 1.0.0, with their API declared shipped; every package's package validation baseline becomes 1.0.0 after the release.
- **Declarations:** every public member is in the package's `PublicAPI.Shipped.txt`, and `PublicAPI.Unshipped.txt` holds only new members from here on.
- **Package validation:** `PackageValidationBaselineVersion` 0.2.0 compares each package with its 0.2.0 release on nuget.org.
  - The breaks made since 0.2.0, the #134 renames and moves, are listed in each project's `CompatibilitySuppressions.xml`: 174 entries in `AvroSharp` (every target framework counts once) and 14 in `AvroSharp.CodeGen`.
  - `AvroSharp.Codecs` and `AvroSharp.Generators` have none.
  - Any other break fails the pack. A deliberate one before 1.0 is added by packing with `-p:GenerateCompatibilitySuppressionFile=true` and committing the file.
  - Since 1.0.0 was released (2026-10-03), the baseline is 1.0.0 for every package, the add-ons included, and the suppression files are gone: any break fails the pack.
- **Decided in the freeze:**
  - [`AvroCodecNames`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroCodecNames.html) moved to [`AvroSharp.Containers`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.html), next to `AvroCodec`; it was the only type outside its folder's namespace.
  - `AvroSharp.CodeGen`'s API is kept as it is: [`CSharpCodeGenerator.Generate`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CSharpCodeGenerator.Generate.html), [`CodeGenOptions`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.html), [`GeneratedSource`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.GeneratedSource.html), [`SchemaFileSet`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.SchemaFileSet.html), [`LogicalTypeMapping`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.LogicalTypeMapping.html) and [`PropertyNaming`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.PropertyNaming.html). The CLI and the source generator use nothing else.
  - Generated union types (#14) and `required`/`init` members (#15) are deferred past 1.0. Each will come as a `CodeGenOptions` setting (and its MSBuild property and CLI option) that is off by default, so code generated without it keeps its shape, and adding it is not a break.

---

## 9. Repo structure and engineering hygiene

```
AvroSharp/
  global.json                      { "sdk": { "version": "10.0.401", "rollForward": "latestPatch" } }
  Directory.Build.props            LangVersion=14; Nullable=enable; ImplicitUsings=enable; TreatWarningsAsErrors=true;
                                   AnalysisLevel=latest-all; EnforceCodeStyleInBuild=true; Deterministic=true;
                                   ContinuousIntegrationBuild (CI); EmbedUntrackedSources; PublishRepositoryUrl;
                                   IncludeSymbols + SymbolPackageFormat=snupkg; GenerateDocumentationFile=true;
                                   IsAotCompatible (net8+ libs); EnableTrimAnalyzer; EnablePackageValidation;
                                   PackageValidationBaselineVersion (after 1.0); Authors/License/Icon/Readme; SignAssembly (public snk, deterministic)
  Directory.Build.targets          PublicApiAnalyzers wiring; InternalsVisibleTo for tests
  Directory.Packages.props         ManagePackageVersionsCentrally=true; CentralPackageTransitivePinningEnabled=true
  .editorconfig                    naming rules, file-scoped namespaces required, IDE/CA severities (errors), StyleCop-lite via IDE rules
  .globalconfig                    analyzer severities (product vs tests via Directory.Build.props conditions)
  .gitattributes / .gitignore / LICENSE / README.md / CONTRIBUTING.md / SECURITY.md / CHANGELOG.md (keep-a-changelog)
  nuget.config                     nuget.org only, packageSourceMapping
  AvroSharp.slnx                   (new XML solution format, SDK 10)
  src/AvroSharp/                   + PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt per TFM group
  src/AvroSharp.CodeGen/  src/AvroSharp.Generators/  src/AvroSharp.Tool/
  src/AvroSharp.Codecs/
  tests/AvroSharp.Tests/  tests/AvroSharp.Spec.Tests/  tests/AvroSharp.Interop.Tests/  tests/AvroSharp.Property.Tests/
  tests/AvroSharp.Generators.Tests/  tests/AvroSharp.CodeGen.Tests/  tests/AvroSharp.NetFramework.Tests/ (net48, references netstandard2.0 build)
  fuzz/AvroSharp.Fuzz/ (SharpFuzz harness)  tests/AvroSharp.AotSmoke/ (PublishAot console)  tests/TestData/ (git submodule or copied share/test/data + own vectors)
  bench/AvroSharp.Benchmarks/   (the gate is built in: --gate)
  samples/
  .github/workflows/ci.yml  release.yml  codeql.yml   (no benchmark workflow: benchmarks are local only)
  .github/dependabot.yml   (nuget + github-actions, weekly, grouped)
```

*Shipped:* besides the differences in the table at the top, `src/` also holds the add-on packages and `tests/` their test projects; `PackageValidationBaselineVersion` is 0.2.0 since the freeze (§8.1); the package icon shipped in #220.

**Analyzers** (all as errors in product code): `Microsoft.CodeAnalysis.NetAnalyzers` (built-in, `AnalysisLevel=latest-all`), `Microsoft.CodeAnalysis.PublicApiAnalyzers`, `Microsoft.CodeAnalysis.BannedApiAnalyzers` (ban `Encoding.UTF8.GetBytes(string)`, `MemoryStream.ToArray`, `Newtonsoft.*`, `System.Reflection.Emit` in core), `Roslynator.Analyzers`, `Meziantou.Analyzer`, `SonarAnalyzer.CSharp` (optional), `Microsoft.VisualStudio.Threading.Analyzers` (async hygiene: VSTHRD), `ErrorProne.NET.Structs` (defensive-copy/`in` misuse — important for ref structs). Generator projects add `Microsoft.CodeAnalysis.Analyzers` (RS rules) and pin `Microsoft.CodeAnalysis.CSharp` 4.x (lowest supported for SDK 8 consumers). StyleCop is not recommended (overlaps IDE rules and fights modern syntax). *Shipped:* NetAnalyzers, PublicApiAnalyzers, BannedApiAnalyzers, Roslynator, Meziantou and the VS Threading analyzers; not Sonar or ErrorProne.NET. The generator references Roslyn 5.x, not 4.x, so it needs the .NET 10 SDK (M2.5 status, #20).

**CI (`ci.yml`)**: matrix `os: [ubuntu-latest, ubuntu-24.04-arm, windows-latest, windows-11-arm]` (the Windows entries were off from 2026-09-25 until #72 turned them back on before the first release), with the 8.0.x, 9.0.x and 10.0.x SDKs installed (`global.json` picks 10). Steps: `dotnet restore` (no NuGet lock files: versions are pinned centrally, packages come only from nuget.org via source mapping, and lock files broke RID-specific publishing), `dotnet build -c Release` (warnings are errors; analyzers and code style are enforced by the build), `dotnet format` is **not** run in CI (it took 251 s on `ubuntu-latest`, longer than the build; it is run locally when needed), `dotnet test` per TFM (`-f net8.0/net9.0/net10.0`, xunit v3 MTP: `dotnet test --project ... -- --coverage`), Windows-only `net48` test project (netstandard2.0 consumer on .NET Framework 4.8.1), `AotSmoke` publish (`-r linux-x64` and `win-x64`) and run, `dotnet pack`, package validation (`EnablePackageValidation`), `Microsoft.DotNet.ApiCompat` baseline after 1.0. Coverage via `Microsoft.Testing.Extensions.CodeCoverage` (Cobertura) → Codecov, threshold 90 % lines on `src/AvroSharp`. `codeql.yml` weekly. No benchmark workflow: benchmarks run locally on request (Section 5.4). *Shipped:* the tests run on TUnit with `dotnet test --solution`, net481 is a target of the test projects on Windows, the AOT smoke tests (core and add-ons) publish and run on all four runners, and coverage is summarized in the job and published as a badge (`build/coverage-summary.sh`), not sent to Codecov, with no threshold.

**Versioning/packaging**: **MinVer** (tag `v1.2.3` → version; pre-release `1.3.0-alpha.0.N` from height) — simpler than Nerdbank for a single-line repo; SourceLink via SDK-built-in (`PublishRepositoryUrl`, `EmbedUntrackedSources`), snupkg symbols, README in package, `PackageReadmeFile`, icon, `release.yml` triggered by tag: build → test → pack → `dotnet nuget push` with trusted publishing (OIDC) or API key secret → GitHub Release with changelog section.

---

## 10. Test strategy

- **Framework**: xUnit.net v3 4.0.x with Microsoft.Testing.Platform v2 (native, standalone test executables, `dotnet test` in SDK 10 MTP mode) **[docs]**. Justification: v3 supports `ValueTask`, cancellation, assembly-level fixtures, parallel-safe theories, MTP integration; NUnit/TUnit are viable but xunit has the widest analyzer/Verify/Roslyn-testing ecosystem.
- **Unit tests** (`AvroSharp.Tests`): schema parse/write/equality; every PCF rule; varint edge cases (0, ±1, `long.MinValue`, over-long encodings rejected); reader over multi-segment `ReadOnlySequence` with boundaries placed at every byte offset (parametrized) — exercised through the public `AvroReader`, not internal helpers; codec roundtrips; resolution rules table-driven from the spec's resolution section; error paths (truncated input, bad union index, negative length, sync mismatch).
- **Spec conformance** (`AvroSharp.Spec.Tests`): `schema-tests.txt` (canonical + CRC-64 for all ~34 entries), `weather*.avro` (null/deflate/snappy/zstd) read → records → re-written → byte-compared where deterministic, `syncInMeta.avro`, `test.avro12`, `messageV1/` single-object vectors, plus hand-written vectors for each logical type and JSON encoding. Test data vendored from `apache/avro/share/test/data` at a pinned commit with a script to refresh.
- **Interop** (`AvroSharp.Interop.Tests`): **against Apache.Avro (C#) only** — test-only references to `Apache.Avro` 1.12.2 (+ its codec packages). Apache Avro's own CI runs cross-language interop between the C# implementation and the other languages, so matching Apache.Avro C# covers those indirectly. That argument holds only where Apache.Avro C# follows the specification; its known deviations are pinned in `ApacheAvroKnownDeviationTests` (AvroSharp follows the specification there). Chr.Avro is not an interop target; it appears only as an ungated reference in benchmarks. For every fixture: AvroSharp writes → Apache reads (Generic + Specific via generated classes committed under tests only), Apache writes → AvroSharp reads; container files with each codec; fingerprints equal to Apache's `SchemaNormalization.Fingerprint64`; resolution outputs equal to Apache's `GenericDatumReader` under writer≠reader.
- **Property-based** (`AvroSharp.Property.Tests`, **CsCheck 4.x**, chosen over FsCheck for speed and C#-first API): random schema generator (bounded depth, named types, unions, logical types) → random values → roundtrip AvroSharp; roundtrip via Apache.Avro (bytes equal); random compatible schema pairs → resolution equals oracle (reflection interpreter and Apache); random byte-split points for `ReadOnlySequence`; canonical-form idempotence (`PCF(PCF(s)) == PCF(s)`) and fingerprint stability.
- **Fuzzing**: SharpFuzz harnesses for `AvroSchema.Parse`, `AvroReader` decode under a random schema corpus, `AvroFileReader` header/blocks, single-object header; run in a nightly workflow (libFuzzer, 30 min per target), crash corpus committed as regression tests. *Status (M2)*: `fuzz/AvroSharp.Fuzz` has libFuzzer targets for schema parsing, generic binary data and generic JSON data, each checking round trips as well as exception types; they run locally (`fuzz/README.md`). The same targets run on every build as a seeded mutation smoke test (`FuzzSmokeTests`), which found invalid UTF-8 inside JSON strings escaping as `InvalidOperationException`. Since then, targets for container files, single-object and registry messages, and resolution were added, and `fuzz.yml` runs every target each night (#34).
- **Benchmarks**: Section 5; the benchmark project references only public packages, so it measures the real product path.
- **AOT smoke** (`AvroSharp.AotSmoke`): `PublishAot=true` console using generated serializers, generic model, container files with zstd; CI asserts zero IL2xxx/IL3xxx warnings and correct output.
- **Real-product-path rule**: no test may reference `internal` helpers except the `Internal` tests folder for varint tables and pool sizing; everything else goes through the public API (enforced by `InternalsVisibleTo` limited to one test project and a code-review checklist).

---

## 11. Phased roadmap (each milestone ends with a tagged pre-release)

- **M0 — Skeleton + CI (1 week)**: repo files in Section 9, empty `AvroSharp` compiling for all 5 TFMs, analyzers on, PublicAPI tracking, MinVer, ci.yml green on Windows/Linux incl. net48 consumer and AOT smoke publish, `0.0.1-alpha` pushed to reserve IDs. *Exit*: green CI matrix; `dotnet pack` produces validated packages; benchmark project scaffold builds.
- **M1 — Schema (2 weeks)**: model, STJ parser/writer, names/namespaces/aliases, defaults, logical types, PCF, CRC-64/MD5/SHA-256, `SchemaBuilder` from types (reflection version). *Exit*: all `schema-tests.txt` entries pass; fingerprints equal Apache.Avro for 100 random schemas; parse benchmark beats Apache.Avro (time and alloc).
- **M2 — Binary primitives + generic model (2 weeks)**: `AvroWriter`/`AvroReader` (span + sequence), `AvroValue`, `GenericRecord`, generic writer/reader, JSON encoding. (The planned spike on expression lambdas over ref structs was dropped with the expression-tree tier.) *Exit*: roundtrip + Apache interop for all primitive/complex/logical types; fuzz harnesses running; generic P/S/N/L benchmarks beat Apache.Avro Generic.

  **SIMD in M2 and later.** Nothing below is measured yet; it is reasoned from the Avro binary format and the BCL. SIMD does not help schema parsing, canonical form or fingerprints (small inputs, computed once and cached), or per-field record decoding (data-dependent and branchy); there the gains come from span-based, allocation-free code.

  | Where | Why it helps | How |
  |---|---|---|
  | Strings | The biggest cost in string-heavy data | Use the BCL's vectorized UTF-8 transcoding (`Encoding.UTF8` span overloads, `Utf8.ToUtf16`); the gain is avoiding Apache.Avro's per-string allocation, not hand-written SIMD. |
  | `float[]` / `double[]` arrays | Avro stores them as raw little-endian, .NET's own layout on x64 and Arm64 | A block becomes one bulk copy (`MemoryMarshal.Cast`) instead of a call per element; big-endian hosts use the vectorized span `BinaryPrimitives.ReverseEndianness`. |
  | `int[]` / `long[]` arrays | Varints can be decoded several at a time | First, detect runs of 16 one-byte varints and decode them together; later, if benchmarks justify it, Masked VByte (Lemire et al.) for mixed-length varints. |
  | Snappy codec CRC-32 | Each Snappy block carries a CRC-32 of the uncompressed data | `System.IO.Hashing.Crc32` (Microsoft, fully managed, netstandard2.0), hardware-accelerated on net8+. |
  | Container-file sync markers | Seeking and splitting search for a 16-byte marker | `Span.IndexOf(marker)`, already vectorized. |
  | Compression | Where the heavy CPU work is | ZstdSharp and Snappier already use SIMD internally. |

  Rules for SIMD code:
  - net8+ only, inside `#if NET8_0_OR_GREATER`, using the portable `Vector128` API so one path covers x64 and Arm64; netstandard gets the scalar version.
  - SIMD and bulk paths (decided in #29, revised in #135). BenchmarkDotNet decides, on uniform and mixed data, on x64. There is no Arm64 machine to benchmark on: Arm64 runs the same `Vector128` paths, and CI tests them there.
    - **Apache.Avro, on every tested CPU, old or new:** a path must be faster than Apache.Avro. This is the absolute rule. A path that isn't is removed.
      - **The one exception, Zen+ CPUs** such as the Ryzen 5 3500U (decided on 2026-10-02, #168): single-value writes may tie with Apache.Avro there, or lose to it in some processes. Zen+ has no memory renaming, so writing one value through `ref AvroWriter` waits on store-to-load forwarding of the writer's position, a floor of about 8–9 cycles per value. Apache.Avro's fast mode on .NET 10 reaches the same floor, and both sides move with code placement between processes. Bulk writes, and every other path, must still be faster than Apache.Avro on Zen+. #168 stays open for a fix that keeps the position in a local across values.
    - **Our plain scalar loop, on current CPUs:** a path must beat it, or at least not be slower by more than 3%, the noise between repeated runs on an idle machine. A path that fails is removed. An old CPU, more than 10 years old, may be slower than the loop: it doesn't hold back a path that wins on current CPUs, as long as the first rule holds there too.
    - **Never a newer CPU for an older one:** a change that makes a current CPU slower is not accepted, even if an older CPU gains from it.
    - **No per-CPU paths by default:** a path isn't selected by ISA or vendor at startup unless a review records why. The varint encoder's word path is one, used where PDEP is fast (`FastBmi2`, #102), because the Ryzen 5 3500U's slow PDEP would otherwise lose.
    - Status: bulk `ReadLongs`/`ReadInts` pass on the current CPUs measured on 2026-09-30 (#135), three rounds each, in BenchmarkDotNet on .NET 10:
      - Timestamps (dense multi-byte values): within the band, at +1.0 to +1.2% of the plain loop on a Ryzen 5 3500U and +1.1 to +2.0% on an i7-12800H.
      - Mixed data: 50% faster than the plain loop on the Ryzen and 33% on the i7.
      - Small values: 83% and 62% faster.
      - Tried and not kept, because each went over 3% on the Ryzen: a scalar batch that doubles up to 64 values on dense data (+3.3 to +3.9%), and the batch in a non-inlined method (+3.7 to +4.4%, though −0.5 to +0.7% on the i7).
      - **The i5-3570K (Ivy Bridge, 2012):** it passes the Apache.Avro rule, and is slower than the plain loop only on dense multi-byte data, which the old-CPU clause allows.
        - Timestamps: 520 µs against 485 µs for the loop (+7.2%) and 1,300 µs for Apache.Avro, so 2.5× faster than Apache. An earlier run the same day gave 534 against 480 µs (+11.3%), and the 2026-09-28 review 536 against 481 µs.
        - Mixed data and small values are faster than the loop there too: 28% and 49% in the 2026-09-28 review.
        - Removing the vector path would give up 33–83% on mixed data and small values on current CPUs.
  - Property tests check that SIMD and scalar paths produce identical results.
  - CI also runs the test suite with `DOTNET_EnableHWIntrinsic=0`, so the scalar fallback is exercised on every run.
- **M2.5 — Schema-file source generator (moved forward from M6)**: the `AvroSharp.CodeGen` engine plus the build-time generator for `.avsc` files passed as `AdditionalFiles`. It emits the C# types and, for each, a serializer and deserializer that call `AvroWriter`/`AvroReader` directly in schema order, with no schema lookups, boxing or virtual calls at runtime. Writer/reader resolution for generated types comes with M3. *Exit*: generated types round-trip and match Apache.Avro C# bytes for the M2 fixtures; snapshot and compile-and-roundtrip tests; incremental-cache generator tests; the generated path is the fastest AvroSharp path in local benchmarks.

  *Status*:
  - **Done:** `src/AvroSharp.CodeGen` (engine) and `src/AvroSharp.Generators` (incremental generator, packed with its dependencies in `analyzers/dotnet/cs`), plus `AvroGeneratedCode`, the small runtime support class that generated code calls.
  - **What gets generated:**
    - a record becomes a `partial class` with get/set properties;
    - an enum becomes a C# enum;
    - a fixed type becomes a class that wraps exactly its size in bytes;
    - `[null, T]` becomes a nullable property, and other multi-branch unions become `object?`.
  - **Guards:** generated readers apply the generic reader's hostile-input limits (block counts checked against the remaining input, zero-size item budget, record depth 128).
  - **Tests:**
    - Roslyn driver tests for diagnostics, snapshots and incremental caching;
    - a consumer project built by the SDK for round trips, byte equality with the generic writer and Apache.Avro, random generic data through every generated type, and hostile input;
    - net481 compiles the generated code against the netstandard2.0 build.
  - **Benchmarks:** measured on 2026-09-26 on an i5-3570K and a Ryzen 5 3500U (docs/reviews/2026-09-26-generated-code.md). Generated code is the fastest AvroSharp path: 1.5-1.9× faster than the generic model for reading and 1.3-1.5× for writing, and 3.7-6.0× faster than Apache.Avro. The exit criterion is met.
  - **Deferred then, done since:** rich logical types (#11), field access by position through `IAvroSpecificRecord` (#10), and the opt-in Apache.Avro compatibility mode (#12, `ISpecificRecord`/`SpecificFixed` with Apache's logical types).
  - **Deferred past 1.0:**
    - generated union classes for multi-branch unions (#14) and `required`/`init` members and records (#15), as §8.1 records;
    - field-run fusion (§4.11).
  - **Requirement:** the generator needs the .NET 10 SDK or Visual Studio 2026 and later, because it runs inside the consumer's compiler, which brings its own Roslyn and System.Text.Json and cannot load newer ones (#20). The generator references a current Roslyn, and it loads AvroSharp's netstandard2.0 build, which references System.Text.Json 10. Measured with a consumer of the packed generator:
    - the .NET 8 SDK (Roslyn 4.11) skips the generator with warning CS9057, so the generated types are missing;
    - the .NET 9 SDK loads it, but it fails with warning CS8785 (FileNotFoundException for System.Text.Json 10.0.0.0);
    - the .NET 10 SDK and Visual Studio 2026 work.

    Older toolsets are not supported: dependency versions (Roslyn, System.Text.Json and the rest) are not held back for them.
- **M3 — Resolution (1.5 weeks)**: `ResolvedSchema`, generic reader consumption, aliases, defaults, promotions. *Exit*: spec resolution table tests + Apache oracle property tests pass; E benchmark (generic) beats Apache.

  *Status*: part 1 is done (`GenericDatumReader.Create(writer, reader)`). The resolved plan is a tree of reader nodes built once per schema pair and cached, rather than a public `ResolvedSchema` type; it covers every rule in the specification's table, with union and enum mismatches deferred to read time as in Java. Part 2 added property tests against Apache.Avro's resolving reader on random schema evolutions (#51). Part 3 added resolution for generated types (`Read(ref reader, writerSchema)`): same-schema data is read directly, other data is transcoded into the type's own encoding (no generic values) and then read by the generated code. The evolution benchmark (#53) passes the gate for both the generic and generated paths. Generated types now read by a resolution plan built once per writer schema (#69): fields are read into the type directly, promoted or remapped in place, and only other differences are transcoded field by field; the generated path (387 ns) is faster than the generic resolving reader (506 ns) on that benchmark.
- **M4 — Attribute-driven generator and `AvroSerializer<T>` API (2 weeks)**: `[AvroSerializable]` on user types generates the schema and the serializer/deserializer; the `AvroSerializer<T>` API over generated code. (The reflection and expression-tree tiers were dropped: no reflection on serialization paths.) *Exit*: typed P/S/N/L/E benchmarks beat Apache Specific and Reflect; zero trim/AOT warnings. **Status:** built in #181 (§6.5), measured in docs/reviews/2026-10-01-attribute-generator.md: 4.7× faster reads and 7.2× faster writes than Apache.Avro, the same as generated code.
- **M5 — Container files + codecs + single-object (2 weeks)**: sync/async writer/reader, `PooledBufferWriter`, null/deflate, Snappy/Zstd packages, `Sync/Seek`, single-object encoding. *Exit*: `weather*.avro`, `syncInMeta.avro`, `messageV1` pass; files written are readable by Apache.Avro C# (which Apache's own CI checks against the other languages); container read/write benchmarks beat Apache for all four codecs; AOT smoke runs.

  *Status*: part 1 is done: synchronous `AvroFileWriter`/`AvroFileReader` (for generic values and, through delegates, generated types) with the null and deflate codecs and pluggable `AvroCodec`s; Apache's `weather.avro`, `weather-sorted.avro` (deflate) and `syncInMeta.avro` are read like Apache.Avro reads them, and files go both ways with Apache.Avro for random schemas. The reader bounds block sizes, including decompressed size, and object counts. Container benchmarks existed then for null and deflate, but had not been run. Part 2 added single-object encoding (`AvroMessage`, `AvroMessageReader`, and the interface now named `IAvroSchemaResolver`), which reads and writes Java's `messageV1` byte for byte. Part 3 added asynchronous reading and writing (`OpenAsync`, `ReadAllAsync`, `WriteAsync`, `FlushAsync`, `DisposeAsync`), with no synchronous I/O on those paths. Part 4 added sync/seek for splittable reads (`PreviousSync`, `Seek`, `Sync`, `PastSync`). Later parts added the rest: pipelined reading through channels ([`ReadAllPipelinedAsync`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader-1.ReadAllPipelinedAsync.html)), the snappy, zstandard, bzip2 and xz codecs ([`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html)), streams of datums without a container ([`AvroStreamReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamReader.html)/[`AvroStreamWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Streams.AvroStreamWriter.html)) and the container fuzz target. The container benchmarks cover every codec; [benchmarks.md](benchmarks.md) has the results.
- **M6 — Code generation, remaining (3 weeks)**: protocols (`.avpr`), codegen options, CLI tool with `dnx`/AOT packaging, snapshot + compile-roundtrip + generator tests. *Exit*: generated P/S/N/L/E benchmarks beat Apache Specific by the target margins and are the best AvroSharp tier; snapshot suite for ≥ 25 schemas; generator incremental-cache tests green; tool published as hybrid RID package.

  *Status*: started. The `avrosharp` tool ships `gen` and `schema canonical`/`fingerprint` (0.2.0) and `schema compat` (#165, 1.0.0-rc.1), as a framework-dependent tool (§6.1). Protocols, `schema from-type` and the hybrid RID package are open (#33), and so are the file commands (#172), all in the 1.x milestone.
- **M7 — Hardening → 1.0 (2 weeks)**: docs site (DocFX or mkdocs), samples, API review (freeze `PublicAPI.Shipped.txt`), package validation baseline, fuzz nightly for 2 weeks with no open crashes, coverage ≥ 90 %. *Exit*: **full benchmark matrix gate passes in a local run on x64**, 1.0.0 tagged.

  *Status*: done. 1.0.0-rc.1 shipped the core packages on 2026-10-01 with their public API frozen (#73, §8.1), and 1.0.0, the first stable release, followed on 2026-10-02 with the add-on packages at the same version (#216). Done: the DocFX docs site and samples (#74), the API freeze and package validation baseline (#73), nightly fuzzing (#34). Additive work is in the 1.x milestone.
- **v1.x**: `AvroSharp.Idl` (.avdl → protocol), Bzip2/Xz codecs if not in 1.0, parallel block decoding, `SearchValues`/SIMD varint batch decode, `Utf8String`-style zero-copy string views. (Bzip2 and xz ship in 1.0 in `AvroSharp.Codecs`, and the SIMD varint batch decode in `ReadLongs`/`ReadInts`; IDL is #174.)

---

## 12. Key risks and open decisions (with recommendations)

1. **Library name / namespace** — DECIDED: `AvroSharp`. NuGet ID appears free **[src, 404 on registration + flat container]**; reserve in M0. (Folder rename `AVroSharp` → `AvroSharp` is optional.)
2. **License** — DECIDED: MIT. See Section 13.
3. **Default typed tier** — DECIDED: source generators only (the schema-file generator in M2.5, the attribute generator in M4). The expression-tree and reflection tiers were dropped, so no reflection runs on serialization paths.
4. **v1 feature set** — Recommend the scope in Section 0; defer IDL and Confluent to 1.x. IDL is a real parser (~2 weeks) and Confluent brings a Confluent.Kafka dependency and its own release cadence. *Decided later:* IDL stays in 1.x (#174); Confluent and the other add-ons ship in 1.0, in this repository, at AvroSharp's version (§3).
5. **Codegen union shape** — Recommend generated closed-hierarchy records with `Match` (works on netstandard2.0; no dependency on OneOf). If C# 15 ships native DUs, add an option later. *Deferred past 1.0* (#14, §8.1).
6. **Generic model `AvroValue` struct vs `object`** — Recommend `AvroValue` (unboxed) with an `object?` convenience indexer; risk is API unfamiliarity vs Apache's `GenericRecord`; mitigated by providing `IDictionary<string, object?>`-like view. *Shipped:* `AvroValue`, without the `object?` indexer or the dictionary view (table at the top).
7. **Performance gate on hosted runners** — Noise may cause flaky gates. Recommend ratio-based comparison (AvroSharp vs Apache measured in the same run) for the hard gate, and absolute regression checks only as warnings until a self-hosted or consistent runner is available. *Decided:* benchmarks don't run on hosted runners at all, only locally (§5.4).
8. **netstandard2.0 surface parity** — Static abstract members unavailable; recommend keeping a registry-based `AvroSerializer.Get<T>()` on all TFMs so code is portable, with the static-abstract fast path as a net8+ bonus. *Shipped:* `AvroTypes` (§6.5.4) is the lookup on every target; `AvroSerializer` stays .NET 8+.
9. **Strong naming** — DECIDED: yes, with the key pair committed (`build/AvroSharp.snk`) for .NET Framework consumers.
10. **Dependency policy** — Core has zero third-party runtime deps on net8+; codecs are satellite packages (Snappier 1.3.x, ZstdSharp.Port 0.8.x, both managed — no native binaries, AOT-friendly **[docs]**). *Shipped:* one `AvroSharp.Codecs` package for all four codecs (decisions at the top).
11. **Bzip2/Xz in 1.0** — DECIDED: in 1.0, on SharpZipLib and Lzma.Net, both managed (decisions at the top).
12. **Test-data vendoring** — Recommend copying a pinned snapshot of `share/test/data` with a refresh script rather than a submodule of the whole Avro repo.

---

## 13. Licensing & provenance (MIT)

- Shipped code is MIT, written from the Avro specification. Apache.Avro (Apache-2.0) is studied for design only; its source is not copied or ported into `src/`. If a port is ever unavoidable, that file keeps its Apache-2.0 header and a `NOTICE` entry — the design avoids needing this.
- Chr.Avro is MIT: derived code is permitted with its copyright notice kept in `THIRD-PARTY-NOTICES.md`; original implementations are still preferred.
- Apache.Avro / Chr.Avro as test- and benchmark-only dependencies do not affect the shipped license (they are not redistributed in AvroSharp packages).
- Vendored `apache/avro/share/test/data` files live under `tests/TestData/apache-avro/` with their own `LICENSE` (Apache-2.0) and `NOTICE`.
- Trademark: "Apache Avro" is an ASF trademark. README/package description: "a .NET implementation of the Apache Avro™ specification"; never "Apache AvroSharp" or anything implying ASF endorsement.

### Where the design lives in the code

- `Directory.Build.props`, `src/Directory.Build.props`: target frameworks, analyzers, AOT/trim, warnings as errors, strong naming, packaging.
- `Directory.Packages.props`: central package versions; `Directory.Build.targets`: the guard that keeps Apache.Avro and Chr.Avro out of shipped projects.
- `src/AvroSharp/IO/AvroWriter.cs`, `AvroReader.cs`: the ref-struct encoding core.
- `src/AvroSharp/Schemas/`: the immutable schema model, the System.Text.Json parser, Parsing Canonical Form and fingerprints.
- `src/AvroSharp.CodeGen/CSharpCodeGenerator.cs`: the schema-to-C# engine behind the source generator.
- `.github/workflows/ci.yml`: the CI matrix. `bench/AvroSharp.Benchmarks`: the locally run performance gate.
