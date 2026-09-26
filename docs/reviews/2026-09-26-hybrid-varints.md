# Hybrid varints (`825f114`): i7-12800H and EPYC 7543

Measures commit `825f114` ("Varints: restore branchy 1-4 byte paths, keep PEXT/PDEP word path for 5-10 bytes"). This commit follows up on [2026-09-26-branchless-varints.md](2026-09-26-branchless-varints.md). The baseline is `ad53209` / `8e4a4a7`, from [2026-09-26-benchmarks-64k.md](2026-09-26-benchmarks-64k.md). Analyzed on 2026-09-26.

| | i7 run | EPYC run |
|---|---|---|
| CPU | Intel Core i7-12800H (Alder Lake) | AMD EPYC 7543 (Zen 3) |
| Code | `825f114` on branch `branchless-varints` | `825f114` (from the version string in the gate output) |
| Job | DefaultJob | DefaultJob |
| Benchmarks | `VarintBenchmarks` (64K random values), `GenericRecordBenchmarks` | same |

The EPYC comparison against Apache passed 18 of 18.

## Per value: baseline → `825f114`

Times are per value (÷ 65,536). The numbers in brackets are the fully branchless commit `57c987c`.

| Case | i7 | EPYC | Verdict |
|---|---|---|---|
| Decode 1 / 2 bytes | 0.61 / 0.89 → 0.61 / 0.89 ns | 0.81 / 1.08 → 0.82 / 1.08 ns | Back to baseline [2.7–2.8] |
| Decode 3 / 4 bytes | 2.11 / 2.12 → 2.15 / 2.10 | 2.70 / 3.24 → 2.71 / 3.24 | Unchanged |
| Decode 5 bytes | 4.39 → **3.92** | 5.25 → **4.62** | 11–12% faster |
| Decode 8 bytes | 3.95 → **3.57** | 4.88 → 4.87 | 10% faster on the i7 |
| Decode mixed 1–10 | 9.20 → **8.19** | 10.18 → **8.56** | 11–16% faster [6.6–6.8] |
| Decode mixed 1–2 | 3.79 → 3.79 | 3.73 → 3.73 | Unchanged [2.7–2.8] |
| Encode 1 / 2 bytes | 0.77 / 0.92 → 0.76 / 0.93 | 1.37 / 1.35 → 1.37 / 1.34 | Baseline; the EPYC comparison passes again (1.36×) |
| Encode 3 / 4 bytes | 2.51 / 2.72 → 2.45 / 2.56 | 3.79 / 3.80 → 3.78 / 3.80 | Unchanged |
| Encode 5 / 8 bytes | 3.42 / 3.46 → **2.88 / 2.85** | 4.89 / 4.88 → **4.35 / 4.34** | 11–18% faster |
| Encode mixed 1–10 | 7.29 → **6.32** | 8.55 → **7.01** | 13–18% faster [4.9–6.0] |
| Encode mixed 1–2 | 3.78 → 3.61 | 3.72 → **4.24** | **14% slower on the EPYC** [1.2–2.5] |

## Whole records (GenericRecord)

| | i7 before → after | EPYC before → after | vs Apache (i7 / EPYC) |
|---|---|---|---|
| Read | 920 → 801 ns (13% faster) | 1,046 → 1,000 ns | 2.0× / 2.6× |
| Write | 339 → 335 ns | 540 → 526 ns | 4.0× / 4.4× |

## Findings

1. **This is a good trade overall.**
   - The common short values (1–4 bytes) are back to full speed.
   - The PEXT/PDEP path gives a clean 10–18% on long values (5–10 bytes, such as timestamps and ids) on both CPUs.
   - Whole records got faster on both machines, and that is the benchmark that matters most.
2. **The odd 5-byte result is fixed on the EPYC,** where 5 bytes now decodes faster than 8 (4.62 vs 4.87 ns). On the i7, 5 bytes is still slightly slower than 8 (3.92 vs 3.57 ns).
3. **There is one regression: mixed 1–2 byte encode on the EPYC** went from 3.72 to 4.24 ns, only 1.33× faster than Apache. The i7 improved slightly on the same case.
   - The commit was meant to leave the 1–2 byte path as it was. The likely suspect is a change in code layout or inlining on Zen 3.
   - Compare the JIT's generated code (`DOTNET_JitDisasm=WriteVarint*`) for `8e4a4a7` and `825f114` on the EPYC.
4. **1-byte encode on the EPYC is still slow,** and this problem is specific to Zen 3. It takes 1.37 ns, 1.8× the i7, and is only 1.36× faster than Apache. The problem predates all the varint work (see [2026-09-26-benchmarks-64k.md](2026-09-26-benchmarks-64k.md), finding 2).
5. **Mixed-length streams give up some of the branchless gain.** Mixed 1–10 is 8.2–8.6 ns now against 6.6–6.8 ns branchless. That is the accepted price of fast 1-byte values.
6. **The 3–4 byte range is untouched and is still the biggest step up in cost:** 2.1 ns on the i7 and 2.7–3.2 ns on the EPYC, against about 1 ns for 2 bytes. Sending 3–4 bytes through the PEXT path might help, but the branchless run made 3 bytes 1.4–1.7× slower. It needs its own measurement.

## Next steps

1. Find the cause of the EPYC mixed 1–2 byte encode regression (finding 3) before merging `branchless-varints`.
2. Profile EPYC 1-byte encode (finding 4). It is the smallest lead over Apache left in the varint benchmarks.
3. Try 3–4 byte values through the PEXT word path, and judge it on `GenericRecordBenchmarks` as well as the varint benchmarks (finding 6).
