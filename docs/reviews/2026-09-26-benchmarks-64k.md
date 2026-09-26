# Benchmarks with 64K random values: i7-12800H and EPYC 7543

A comparison of the first two full runs that use 64K random values per operation, so the CPU cannot memorize the data (see §1.2 of [2026-09-25-performance.md](2026-09-25-performance.md)). Analyzed on 2026-09-26.

| | i7 run | EPYC run |
|---|---|---|
| CPU | Intel Core i7-12800H (Alder Lake) | AMD EPYC 7543 (Zen 3), 2 CPUs, 128 logical cores |
| OS | Windows 11 | Ubuntu 22.04.5 |
| Runtime | .NET SDK 10.0.400 | .NET 10.0.12 (SDK 10.0.401) |
| Code | Almost certainly `ad53209` ("Cheap performance wins"); the run started 5 minutes after that commit | Unknown. Array writes are about 2× slower than on the i7, where everything else is about 1.3× slower, so it probably predates `ad53209`. |

All times are per value (per operation ÷ 65,536). The benchmarks run on one thread, so the EPYC's core count doesn't matter. On the same code the EPYC is about 1.25–1.35× slower than the i7, which fits the clock-speed difference.

## Varint: mixed lengths are the real bottleneck

| Case | i7 decode | EPYC decode | i7 encode | EPYC encode |
|---|---|---|---|---|
| 1 byte | 0.61 ns | 0.81 ns | 0.77 ns | 1.37 ns |
| 2 bytes | 0.89 | 1.08 | 0.92 | 1.35 |
| 3 / 4 bytes | 2.11 / 2.12 | 2.70 / 3.24 | 2.51 / 2.72 | 3.79 / 3.80 |
| 5 / 8 / 10 bytes | 4.39 / 3.95 / 4.07 | 5.25 / 4.88 / 5.15 | 3.42 / 3.46 / 3.38 | 4.89 / 4.88 / 5.42 |
| **Mixed 1–2 bytes** | **3.79** | **3.73** | **3.78** | **3.72** |
| **Mixed 1–10 bytes** | **9.20** | **10.18** | **7.29** | **8.55** |

1. **Mixing lengths costs far more than the lengths themselves.**
   - A random mix of 1- and 2-byte values takes 3.7–3.8 ns. That is 4–6× slower than either length alone (0.6–0.9 ns).
   - A random mix of 1 to 10 bytes takes 9–10 ns, 2.3× slower than the slowest uniform length.
   - Both machines agree, so this is branch misprediction, not a CPU quirk.
   - This is what the branchless PEXT/PDEP change (review §1.1) targets. The reviewer's test measured about 2.8 ns decode and 1.7 ns encode regardless of the mix.
   - Mixed 1–2 byte values are the most common real-world pattern (string lengths, small ints, union indexes), which makes this the strongest case yet for §1.1.
2. **Encoding 1- and 2-byte values on the EPYC seems to carry a fixed cost of about 1.35 ns per call.** Both lengths cost the same, and 1-byte encode is 1.7× slower than decode, against 1.26× on the i7. One guess is that updating `_buffered` in memory on every call is especially slow on Zen 3. This is unverified and needs profiling.
3. **5 bytes decodes slower than 8 on both machines** (4.39 vs 3.95 ns on the i7). The 5-to-8-byte path handles 5 bytes badly.

## Bulk reads (i7): still losing on mixed data

| Data | Bulk `ReadLongs` | Plain loop | Apache |
|---|---|---|---|
| SmallValues | 0.61 ns | 0.67 ns | 1.81 ns |
| Mixed (90% small, 10% timestamps) | **2.03 ns** | **1.63 ns** | 3.77 ns |
| Timestamps | 3.86 ns | 3.92 ns | 7.90 ns |

- Timestamps have caught up with the plain loop; they were 10% slower before.
- Mixed data is still 25% slower than the plain loop.
- The EPYC comparison against Apache doesn't record the plain loop, so it can't confirm this there. Its bulk numbers are 0.74 / 2.09 / 5.26 ns for SmallValues / Mixed / Timestamps.

## What the cheap performance wins changed (i7)

Showcase results on the same machine, from the 21:48 run to the 22:29 run:

| Scenario | Before | After |
|---|---|---|
| DoubleArray write | 2,309 ns | **787 ns (2.9× faster)** |
| IntArray / LongArray write | 1,836 / 2,051 ns | 1,042 / 998 ns (1.8–2.1×) |
| Telemetry write | 77 ns | 51 ns (1.5×) |
| Telemetry / Counters read | 93 / 68 ns | 79 / 55 ns |

The 21:48 run was a short, noisy job, so these before/after figures are approximate. The size of the array-write speed-up (2–3×) is well beyond that noise.

## How far ahead of Apache

| Benchmark | i7 | EPYC |
|---|---|---|
| Varint decode, Mixed 1–2 / 1–10 | 1.80× / 1.69× | 2.05× / 1.78× |
| Varint encode, Mixed 1–2 / 1–10 | 1.44× / 1.79× | 1.52× / 1.55× |
| Strings decode / encode | **1.17× / 1.13×** | **1.18× / 1.29×** |
| GenericRecord read / write | 1.68× / 3.95× | 2.45× / 4.18× |
| Showcase IntArray read | **1.79×** | 2.36× |
| Showcase DoubleArray write | 21.3× | 14.6× |

- **Strings** are the smallest lead. UTF-8 conversion and allocation dominate, and the single-pass `WriteString` (review §1.5) is the only real lever left.
- **Reading primitive arrays** is 1.8× Apache on the i7, with half of Apache's allocation. Typed primitive arrays (review §2.1) are the fix.
- **EPYC 1-byte encode** is only 1.36× Apache (finding 2 above).
- The EPYC comparison passed 41 of 41, with every result faster than Apache. Apache is relatively slower on the EPYC, so the ratios there look better than on the i7.

## Next steps

1. Do PEXT/PDEP next (review §1.1). The mixed-length results on both machines are the case for it, and its main risk (slow PEXT/PDEP on Zen 1/2) doesn't apply to Zen 3.
2. Profile 1-byte encode on the EPYC (finding 2) and 5-byte decode (finding 3).
3. For mixed data, make bulk `ReadLongs` fall back to the plain loop when runs of small values are short, or do the SIMD decode (review §1.3).
4. Record which commit each run used, and add the plain-loop bulk read to the Apache comparison output so machines can be compared directly.
