# Generated code vs the generic model (M2.5 exit)

Measures the serializers that the `.avsc` source generator emits (PR #9) against AvroSharp's generic model and Apache.Avro's generic model, on the same schema and data. This checks the M2.5 exit criterion: "the generated path is the fastest AvroSharp path". Tracked in #22. Analyzed on 2026-09-26.

| | i5 run | NAS run |
|---|---|---|
| CPU | Intel Core i5-3570K (Ivy Bridge), x86-64-v2 | AMD Ryzen 5 3500U (Zen+), x86-64-v3 |
| OS / runtime | Windows 10, .NET 10.0.12 | Ubuntu 24.04, .NET 10.0.7 |
| Code | `8532f1d` (#36) | `167e4b7` (the head of #36 before merging; same code) |
| Job | DefaultJob | DefaultJob |
| Benchmark | `GenericRecordBenchmarks` | same |

The workload is one `Order` record with:
- a `long` id, a string, a double, an int and a boolean;
- a nullable string;
- 10 nested `Line` records;
- 64 `long` counters;
- a 2-entry map.

The generated rows use the same schema, from `bench/AvroSharp.Benchmarks/Schemas/order.avsc`. Setup checks that the generated type reproduces the generic encoding byte for byte.

## Results

Times are per record.

| Row | i5 | NAS | Allocated (both) |
|---|---:|---:|---:|
| Apache read | 3,504 ns | 3,863 ns | 5,032 B |
| Generic read | 1,534 ns | 1,573 ns | 3,424 B |
| **Generated read** | **806 ns** | **1,059 ns** | **2,192 B** |
| Apache write | 2,933 ns | 3,478 ns | 5,608 B |
| Generic write | 662 ns | 946 ns | 0 |
| **Generated write** | **493 ns** | **639 ns** | **0** |

| Speed-up | i5 | NAS |
|---|---:|---:|
| Generated vs generic, read | 1.90× | 1.49× |
| Generated vs generic, write | 1.34× | 1.48× |
| Generated vs Apache, read | 4.35× | 3.65× |
| Generated vs Apache, write | 5.95× | 5.44× |

## Findings

1. **The M2.5 exit criterion is met on both machines.** Generated code is the fastest AvroSharp path for both reading and writing. Every AvroSharp row passes the gate (faster than Apache, allocating no more).
2. **Generated read allocates 36% less than generic read** (2,192 B against 3,424 B), and 56% less than Apache. What remains is the object graph a reader has to create: the `Order`, 10 `Line` objects, the strings, the lists and the dictionary.
3. **Where the gain comes from.** This is reasoned from the code, not profiled:
   - generated code has no per-field node dispatch, no `AvroValue` wrapping and no kind checks;
   - it reads the `long` array straight into a `List<long>` in bulk;
   - reading builds plain objects instead of `GenericRecord`s and `AvroValue` lists, which is why read gains more than write on the i5.
4. **The two machines agree** within what you'd expect from different CPUs and operating systems, so one run per machine was enough for this comparison. Its margins are structural rather than instruction-level, unlike the varint work.

## An invalid first run

The first i5 run of `2723096` (the #9 merge) reported:
- generic write at 1,341 ns with 1.8 KB allocated, against 678 ns and no allocation on `825f114`;
- generated write also allocating 1.8 KB.

That run measured the wrong AvroSharp build:
- BenchmarkDotNet builds every project into one shared `/p:OutDir`. Since #9, the benchmark project also builds the source generator, whose netstandard2.0 copy of `AvroSharp.dll` overwrote the net10.0 one.
- A reproduced build confirmed it: the loaded assembly's `TargetFrameworkAttribute` was `.NETStandard,Version=v2.0`.
- An allocation probe against the real net10.0 build showed 0 B per write at about 713 ns, so the library had not regressed.

#36 fixed the build. The benchmarks also refuse to start when they load a netstandard AvroSharp, and the version line now prints the target framework. The AvroSharp rows of that run are discarded above; its Apache rows were unaffected.

## Not covered

- Only one schema was measured. A record dominated by strings, or by unions (which generated code maps to `object?` for now), would shift the balance.
- There is no comparison with Apache's specific (generated) path. It needs Apache-generated classes, which will arrive naturally with the compatibility mode (#12).
