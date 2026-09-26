# Varint benchmarks on AMD EPYC 7543 (Zen 3)

A `VarintBenchmarks` and gate run on a second machine, analyzed on 2026-09-26. It follows the review in [2026-09-25-performance.md](2026-09-25-performance.md).

**Environment**

| | |
|---|---|
| CPU | AMD EPYC 7543 (Zen 3, 1.50 GHz base), 2 CPUs, 64 physical and 128 logical cores |
| OS | Ubuntu 22.04.5 LTS |
| Runtime | .NET 10.0.12, X64 RyuJIT x86-64-v3, BenchmarkDotNet 0.15.8, DefaultJob |

The core count doesn't matter here: the benchmarks run on one thread. The machine is interesting because it is a different microarchitecture from the i7-12800H (Alder Lake) behind the original review.

## Caveat: the run used older benchmark code

The parameter list has no mixed-length varint case. The times also only make sense for about 1,000 values per operation, since 815 ns for 64K values would be about 0.01 ns per value. So this run predates `4b03086` ("Benchmarks: 64K random values, mixed-length varints"). Every length is uniform and easy for the CPU to predict, so treat these numbers as best-case (see §1.2 of the review).

## Varint time per value (1,000 values)

| Bytes | Decode | vs Apache | Encode | vs Apache |
|---|---|---|---|---|
| 1 | 0.82 ns | 3.7× faster | 1.38 ns | **1.18× faster** |
| 2 | 1.09 ns | 4.0× | 1.67 ns | 1.79× |
| 3 | **2.98 ns** | **2.1×** | **3.79 ns** | **1.25×** |
| 4 | 3.25 ns | 2.4× | 3.79 ns | 1.85× |
| 5 | **5.71 ns** | 1.7× | 4.87 ns | 1.89× |
| 8 | 5.40 ns | 3.1× | 4.88 ns | 3.2× |
| 10 | 5.67 ns | 3.6× | 5.41 ns | 3.3× |

## Findings

1. **Speed drops sharply at 3 bytes.** Decoding goes from 1.09 to 2.98 ns (2.7×) and encoding from 1.67 to 3.79 ns (2.3×). The 3- and 4-byte path is the weakest point against Apache, as it was on the i7, where 3-byte encoding took 0.88× Apache's time. Values from 16K to 2M, such as ids and counters, land in this range.
2. **All lengths from 5 to 10 bytes decode in about 5.5 ns**, and 5 bytes is actually slower than 8. This is the path the branchless PEXT/PDEP change replaces (review §1.1). On the i7 that change measured about 2.7 ns for decode and 1.7 ns for encode, flat across lengths.
3. **Zen 3 can use PEXT/PDEP.** The main risk in review §1.1 is that PEXT and PDEP are microcoded and slow on AMD Zen 1/2 (about 18 cycles). Zen 3 runs them in a few cycles, so this machine is a valid target for §1.1. Only older EPYCs (7xx1/7xx2) would need the shift-and-mask fallback.
4. **Encoding 1-byte values is surprisingly slow:** 1.38 ns versus 0.82 ns to decode, and only 1.18× faster than Apache. The write fast path seems to carry per-call overhead, possibly the capacity check or the `_buffered` field store. This is worth profiling; it affects every count, union index and enum ordinal.
5. **The smallest leads over Apache are 1-byte encode (1.18×), 3-byte encode (1.25×) and string decode (1.32×).** String decoding is dominated by `Encoding.UTF8.GetString` and the string allocation, and allocates the same 74 KB as Apache, so there is little room left there. The other two are real gaps in the code.
6. **The gate comparison against Apache passed 26 of 26.** Encoding wins are larger than decoding wins:

| Workload | Decode | Encode |
|---|---|---|
| Longs | 3.41× | 3.93× |
| RealisticLongs | 2.96× | 2.82× |
| Mixed | 1.76× | 2.57× |
| Strings | 1.32× | 1.93× |

Schema parsing is 1.75–2.13× faster than Apache and allocates about a third as much.

## Next steps

- Re-run on the EPYC with the current branch (64K random values, mixed lengths, the §0 input limits) to get realistic numbers. Include `BulkReadBenchmarks`, which is missing from this run.
- Run the PEXT/PDEP test benchmark from the review on the EPYC to confirm the §1.1 gains on Zen 3 before adopting it.
- Profile the 1-byte encode path (finding 4).
