# Branchless varints (`57c987c`): i7-12800H and EPYC 7543

Measures commit `57c987c` ("Branchless varints: 1-2 bytes inline, 3-8 bytes as one word with PEXT/PDEP"), which implements review §1.1 ([2026-09-25-performance.md](2026-09-25-performance.md)). The baseline is [2026-09-26-benchmarks-64k.md](2026-09-26-benchmarks-64k.md). Analyzed on 2026-09-26.

| | i7 run | EPYC run |
|---|---|---|
| CPU | Intel Core i7-12800H (Alder Lake) | AMD EPYC 7543 (Zen 3) |
| Code | `57c987c` on branch `branchless-varints` | `57c987c` (from the version string in the gate output) |
| Job | ShortRun: 3 iterations, 3 warmups | ShortRun: 3 iterations, 3 warmups |
| Benchmark | `VarintBenchmarks`, 64K random values | same |

**Reliability.** The AvroSharp numbers are tight (under 1% error) on both machines. The local Apache baselines are not reliable: for example, 3-byte decode shows 936 ± 3,120 µs. So this analysis compares AvroSharp against its own previous run. The one Apache comparison that matters is the EPYC's, which is consistent with its previous run.

## Before and after

Times are per value (÷ 65,536). "Before" is `ad53209` on the i7 and the previous 64K run on the EPYC.

| Case | i7 before → after | EPYC before → after | Change |
|---|---|---|---|
| Decode 1 byte | 0.61 → **2.68 ns** | 0.81 → **2.82 ns** | **3.5–4.4× slower** |
| Decode 2 bytes | 0.89 → 2.69 | 1.08 → 2.83 | 2.6–3× slower |
| Decode 3 bytes | 2.11 → 3.65 | 2.70 → 3.73 | 1.4–1.7× slower |
| Decode mixed 1–2 | 3.79 → 2.68 | 3.73 → 2.81 | 1.3–1.4× faster |
| Decode mixed 1–10 | 9.20 → 6.63 | 10.18 → 6.79 | 1.4–1.5× faster |
| Encode 1 byte | 0.77 → 1.18 | 1.37 → **2.47** | 1.5–1.8× slower; **the EPYC fails vs Apache (0.76×)** |
| Encode 2 bytes | 0.92 → 1.15 | 1.35 → 2.47 | 1.25–1.8× slower |
| Encode 3 bytes | 2.51 → 2.50 | 3.79 → 3.80 | no change |
| Encode mixed 1–2 | 3.78 → **1.15** | 3.72 → 2.47 | 1.5–3.3× faster |
| Encode mixed 1–10 | 7.29 → 4.86 | 8.55 → 6.04 | 1.4–1.5× faster |

The EPYC comparison against Apache passed 9 of 10. It failed 1-byte encode: 162,100 ns vs 122,846 ns (0.76×).

## Findings

1. **Removing the 1-byte branch made each value wait for the previous one.**
   - The new decoder always reads two bytes and advances the position by `1 + more`. The next read can't start until the current value has been decoded.
   - With the old `< 0x80` branch, the CPU guessed the next position and ran ahead.
   - The result is a flat ~2.7 ns for every 1- and 2-byte value on both machines.
   - This matches the review's test almost exactly: fully branchless measured 2.87 ns on all-1-byte data, against 0.75 ns with the check. Review §1.1 said to keep the 1-byte check.
2. **This is probably a bad trade for real data.**
   - One-byte values dominate real records: counts, union indexes, enum ordinals, small ints. Each gets about 2 ns slower, while only randomly mixed 1–2 byte streams save about 1.1 ns.
   - In a real record, consecutive varints belong to different fields, and each field's length is usually stable, so the CPU can usually predict it per field.
   - `Mixed1-2`, where the length is random for every value, is the worst case for branchy code and not typical of records.
3. **The PEXT/PDEP path for 3–8 bytes works.** Mixed 1–10 is 1.4–1.5× faster on both machines. That includes Zen 3, where PEXT and PDEP are fast.
4. **Encoding 1–2 bytes on Zen 3 is now a real problem.**
   - The EPYC spends 2.47 ns per value, 2.1× the i7, and is slower than Apache.
   - It was already 1.8× the i7 before this commit, so something Zen-specific in the write path got worse.
   - The cause is unknown. Look at the JIT's generated assembly on the EPYC (`DOTNET_JitDisasm=WriteVarint*`).

## Recommendations

1. **Put the 1-byte fast path back** in both the reader and the writer, before the 2-byte branchless path:
   ```csharp
   if (b < 0x80) { _position = position + 1; return b; }
   ```
   Keep PEXT/PDEP for 3–8 bytes. That should give back roughly 0.6 ns for 1-byte values and keep most of the mixed 1–10 gain. Mixed 1–2 will get slower again, which is the right trade.
2. **Judge it on whole records,** not only the varint microbenchmarks: `GenericRecordBenchmarks`, and the Telemetry and Counters scenarios in `ShowcaseBenchmarks`. They reflect how lengths actually vary per field.
3. **Fix EPYC 1-byte encode before merging.** Its comparison against Apache now fails.
4. **Re-run with the default job.** Three iterations leaves the Apache baselines too noisy to trust.
