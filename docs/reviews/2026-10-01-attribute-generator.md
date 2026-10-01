# [AvroSerializable] types vs generated code (#31)

This run measures the serializers of a C# type marked `[AvroSerializable]` (the attribute-driven generator, #31). They are compared on the same record with the type generated from `.avsc`, the generic model, and Apache.Avro's generic model. The exit criterion is that the typed path beats Apache.Avro. The attribute-driven generator emits through the `.avsc` generator's serializer code, so it should also match generated code. Measured on 2026-10-01.

| | |
|---|---|
| CPU | AMD EPYC 7543 (Zen 3), 2 sockets, x86-64-v3 |
| OS / runtime | Ubuntu 22.04, .NET 10.0.12 (SDK 10.0.401) |
| Code | `baacd05` (the head of #181 at the time) |
| Job | DefaultJob, one 4-core lane (one CCD) per socket, run at the same time |
| Benchmark | `GenericRecordBenchmarks` |

**The workload:** one `Order` record with:
- a `long` id, a string, a double, an int and a boolean;
- a nullable string;
- 10 nested `Line` records;
- 64 `long` counters;
- a 2-entry map.

**How the twin is set up:** the `[AvroSerializable]` twin is `bench/AvroSharp.Benchmarks/AttributedTypes.cs`, with the same fields as `Schemas/order.avsc`, written as C# properties with camelCase field names. Setup checks that it reproduces the generic encoding byte for byte, as it does for the generated type.

## Results

Times are per record, for socket 0 / socket 1. Allocations are the same on both.

| Row | Read | Allocated | Write | Allocated |
|---|---:|---:|---:|---:|
| Apache.Avro (baseline) | 2,513 / 2,536 ns | 5,032 B | 2,270 / 2,454 ns | 5,608 B |
| Generic model | 800 / 775 ns | 2,912 B | 504 / 539 ns | 0 B |
| Generated from `.avsc` | 553.5 / 557.6 ns | 2,048 B | 316.0 / 316.7 ns | 0 B |
| **`[AvroSerializable]`** | **526.8 / 536.6 ns** | **2,048 B** | **316.9 / 331.8 ns** | **0 B** |

## Findings

- **Against Apache.Avro:**
  - Reads are 4.7–4.8× faster.
  - Writes are 7.2–7.4× faster.
  - The type allocates 41% of Apache.Avro's memory per read, and nothing per write.
  - So the #31 exit criterion holds on this machine.
- **Against generated code:**
  - Reads are 3.8–4.8% faster, on both sockets.
  - Writes are the same on socket 0 (316.9 against 316.0 ns), and 4.8% slower on socket 1 (331.8 against 316.7 ns).
  - The serializers are the same code, so the difference is code and heap placement within the lanes' noise, which is 3–5% between lanes of one build.
  - Allocations are identical.
- **The generated code is the same:** apart from the type's own declarations, the attribute-driven generator emits through `CSharpCodeGenerator.GenerateDeclared`, which is the `.avsc` path's `EmitRecord` without the property declarations.

The rows are gated like the others (`AvroSharp_Attributed_Read` and `AvroSharp_Attributed_Write` against Apache.Avro's baseline). So every full gate run checks this result again.
