# perf/varints-parse at 41d6483: second run on the i5-3570K and the nas

A second run of the `perf/varints-parse` branch on the same two machines as the [first run](2026-09-28-varints-parse.md), analyzed on 2026-09-28. It measures `41d6483`, which moved the inline 3- to 8-byte word store behind `FastBmi2` and inlined every length into `WriteLongs`/`WriteInts`. This time the full suite ran, apart from ResolutionBenchmarks.

| | Desktop | nas |
|---|---|---|
| CPU | Intel Core i5-3570K (Ivy Bridge), x86-64-v2, no BMI2 | AMD Ryzen 5 3500U (Zen+), x86-64-v3, microcoded PDEP/PEXT |
| `FastBmi2.IsSupported` | false (no BMI2) | false (AMD family below 0x19) |
| OS, runtime | Windows 10 22H2, .NET 10.0.12 | Ubuntu 24.04.5 LTS, .NET 10.0.7 |
| Code | `41d6483`, clean tree; the run started 3 minutes after the commit | `41d6483`, clean tree |
| Size | 165 benchmarks in 58 minutes | 170 benchmarks in 59 minutes (Varint adds the 10-byte case) |

Neither machine has fast PDEP, so on both of them every varint above 2 bytes now goes through the out-of-line `WriteVarintMulti`. Neither run covers the `FastBmi2` path (Zen 3 and later, Intel with BMI2).

There are three runs per machine:

- **main:** the 2026-09-27 runs, before this branch and before the main commits of 2026-09-28.
- **7311fd2:** the first run of this branch.
- **41d6483:** this run.

"41d6483 against 7311fd2" isolates the new commit. "Against main" also includes the main commits of 2026-09-28 (`97d0fdb` typed primitive arrays, `594f543` generated code, and others).

## Summary

- **The gate passes on the i5 (104 AvroSharp rows) and fails one row of 107 on the nas: 1-byte encode, 1.09×.** That row was already failing on main (1.15×), and this branch did not change it (216.8 µs against 212.4 µs on main).
- **Fixed by 41d6483:**
  - 3- and 4-byte encode on the i5 is now faster than on main (−11% and −15%).
  - The bulk writers are now at least as fast as one-at-a-time writes, for every length on both machines.
- **New in 41d6483: 8-byte encode and Mixed1-10 are slower than on main on both machines,** by +11–12% on the i5 and +18% on the nas. Against 7311fd2 they are +34% to +57% slower.
- **2-byte encode on the nas is 26% slower than on main.** The commit set out to fix this, and it got worse. The call site should compile the same as main's, so the cause is unknown; see "How much single rows move".
- **Record writes (BinaryEncoding) are within ±6% of main on both machines.** The 8-byte and Mixed1-10 regression does not show up at record level in these benchmarks.
- **Several large gains against main come from main, not from this branch:** Showcase primitive arrays are 43–94% faster, and container reads 17–28% faster.

## Varint encode, one value at a time

| Bytes | i5 main | i5 7311fd2 | i5 41d6483 | vs main | nas main | nas 7311fd2 | nas 41d6483 | vs main |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 133.4 µs | 132.6 µs | 133.3 µs | −0.1% | 212.4 µs | 217.1 µs | 216.8 µs | +2.1% |
| 2 | 139.2 µs | 140.3 µs | 136.4 µs | −2.0% | 170.2 µs | 192.7 µs | 215.2 µs | **+26.4%** |
| 3 | 325.1 µs | 372.4 µs | 287.9 µs | −11.4% | 362.1 µs | 320.4 µs | 330.6 µs | −8.7% |
| 4 | 356.1 µs | 371.3 µs | 302.6 µs | −15.0% | 370.1 µs | 318.5 µs | 328.9 µs | −11.1% |
| 5 | 498.5 µs | 372.0 µs | 327.3 µs | −34.3% | 421.2 µs | 310.4 µs | 344.3 µs | −18.3% |
| 8 | 498.1 µs | 371.4 µs | 557.9 µs | **+12.0%** | 415.9 µs | 312.0 µs | 488.7 µs | **+17.5%** |
| 10 | not run | not run | not run | | 380.0 µs | 411.4 µs | 429.6 µs | **+13.1%** |
| Mixed1-10 | 657.2 µs | 544.9 µs | 730.5 µs | **+11.1%** | 604.4 µs | 513.9 µs | 711.0 µs | **+17.6%** |
| Mixed1-2 | 302.5 µs | 313.9 µs | 299.2 µs | −1.1% | 288.6 µs | 330.3 µs | 299.3 µs | +3.7% |

- **3 to 5 bytes:** the 4-byte store in `WriteMultiByteVarint` beats main's byte stores (3, 4) and main's word path (5) on both machines.
- **6 to 8 bytes: slower than main.** Main's out-of-line call made two compares and then stored the word. `WriteMultiByteVarint` first builds the 4-group `low` value, then tests three lengths (3, 4, 5), and only then spreads and stores the word. The `low` computation is wasted for these lengths. This is reasoned from the code, not profiled. Computing `low` inside the `value < 1UL << 35` branch would test it.
- **Mixed1-10** is dominated by the longer values, and moves with the 8-byte row.
- **10 bytes on the nas:** +13% against main, and +4% against 7311fd2. The 9- and 10-byte tail now also sits behind the three short-length tests.
- **2 bytes on the nas:** the 1- and 2-byte code is the same as main's. The `FastBmi2` test is a `static readonly bool`, which the optimizing JIT folds away, and the out-of-line call is a `void` instance call, as on main. Yet the row went from 170 µs (main) to 193 µs (7311fd2) to 215 µs (41d6483). The i5 does not show it (−2% against main). The cause is not known.

### How much single rows move between runs

Some rows moved as much with unchanged code:

- **Varint decode:** neither commit on this branch touches single-value decode. Yet 3-byte decode moved −17.0% on the i5 (285.8 to 237.3 µs) and +11.9% on the nas (272.9 to 305.3 µs) between the 7311fd2 and 41d6483 runs.
- **GenericRecord `AvroSharp_Read`** on the i5 moved +11.5% (1,143 to 1,275 ns) with no read change.
- **Apache.Avro's times on the nas** moved even more: 8-byte encode went from 1,456 to 2,409 µs (+65%), and Mixed1-10 from 1,792 to 1,138 µs.

So a single varint row that moves by 10–15% on one machine is not evidence on its own. The 8-byte and Mixed1-10 regressions show up on both machines and move by 11–57%, so they are. The nas's 2-byte row is on one machine only, and needs a repeat run before acting on it.

## Varint encode, bulk (`WriteLongs`)

| Bytes | i5 7311fd2 | i5 41d6483 | Δ | i5 single | nas 7311fd2 | nas 41d6483 | Δ | nas single |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 69.9 µs | 73.6 µs | +5.4% | 133.3 µs | 153.3 µs | 153.8 µs | +0.3% | 216.8 µs |
| 2 | 88.9 µs | 98.5 µs | +10.9% | 136.4 µs | 159.2 µs | 164.1 µs | +3.1% | 215.2 µs |
| 3 | 212.1 µs | 159.3 µs | −24.9% | 287.9 µs | 221.4 µs | 164.2 µs | −25.8% | 330.6 µs |
| 4 | 209.1 µs | 164.1 µs | −21.5% | 302.6 µs | 221.0 µs | 191.0 µs | −13.6% | 328.9 µs |
| 5 | 226.4 µs | 179.1 µs | −20.9% | 327.3 µs | 250.6 µs | 203.0 µs | −19.0% | 344.3 µs |
| 8 | 489.0 µs | 465.5 µs | −4.8% | 557.9 µs | 612.7 µs | 388.3 µs | −36.6% | 488.7 µs |
| 10 | not run | not run | | | 383.5 µs | 323.0 µs | −15.8% | 429.6 µs |
| Mixed1-10 | 631.8 µs | 621.8 µs | −1.6% | 730.5 µs | 682.1 µs | 585.9 µs | −14.1% | 711.0 µs |
| Mixed1-2 | 276.3 µs | 289.2 µs | +4.7% | 299.2 µs | 313.3 µs | 307.1 µs | −2.0% | 299.3 µs |

- **The bulk path is now faster than single writes for every length on both machines,** except the nas's Mixed1-2, where the two are equal (307 against 299 µs). This was follow-up 2 of the first run.
- On the i5, the 1- and 2-byte bulk rows are 5–11% slower than at 7311fd2. The loop now inlines every length, so the 1- and 2-byte fast path sits in a larger loop body. That explanation is reasoned from the code; given the variation above, a repeat run would confirm or rule it out.
- The nas's 8-byte bulk row was bimodal at 7311fd2, so its −36.6% is measured against an uncertain mean.

## Record and file benchmarks

These are the rows the first run said it lacked (BinaryEncoding, Container, Showcase). None of them ran at 7311fd2, so they compare with main.

### BinaryEncoding

| Case | i5 vs main | i5 faster than Apache | nas vs main | nas faster than Apache |
|---|---:|---:|---:|---:|
| Encode Longs | −5.6% | 1.94× | +1.3% | 2.59× |
| Encode RealisticLongs | +2.4% | 1.55× | −2.5% | 2.23× |
| Encode Mixed | +0.4% | 1.60× | −2.6% | 1.68× |
| Encode Strings | +0.4% | 1.22× | −1.1% | 1.33× |
| Decode Longs | −2.9% | 1.95× | +2.3% | 2.13× |
| Decode RealisticLongs | −1.4% | 1.80× | −3.0% | 1.89× |
| Decode Mixed | +0.4% | 1.60× | +3.9% | 1.50× |
| Decode Strings | +0.7% | 1.27× | +2.4% | 1.19× |

Record-level encode is within ±6% of main on both machines. The 8-byte and Mixed1-10 regression doesn't show here. The nas's Apache.Avro encode rows were flagged multimodal (mValue 5.2), so their ratios there are less certain than the AvroSharp times.

### Records

Against 7311fd2:

- **WideRecord `AvroSharp_Generated_Write` is +6.2% on the i5 (681 to 723 ns) and +5.4% on the nas (696 to 734 ns).** It moved the same way on both machines, and the commit changes the write path, so it is probably real.
- GenericRecord `AvroSharp_Generated_Write` is +1.2% and +2.8%.
- The other record rows are within ±4%, apart from two on the i5: GenericRecord `AvroSharp_Generated_Read` (−7.0%) and `AvroSharp_Read` (+11.5%). Neither read path changed; see "How much single rows move".

### Container

Against main, generic container reads (sync, async and pipelined) are 17–28% faster on the i5 for null, deflate, snappy, zstandard and xz, and 15–27% faster on the nas. Generated reads are 3–8% faster. The generic reads allocate 2,855 KB, against 3,356 KB on main. Writes on the i5 range from −12% to +2%; on the nas they are 11–23% faster. On the nas, only deflate and null have an earlier result, for reads and writes. bzip2 is dominated by compression: its reads on the i5 range from −3% to +8%, and its writes take 0.94–0.96× Apache.Avro's time on both machines, the thinnest margin in the suite.

This branch doesn't touch the read path, and the allocation drop matches the typed primitive arrays of `97d0fdb`. So the read gains probably come from main, but that has not been checked.

On the nas, pipelined async reads take 2.2× as long as sequential reads for the fast codecs (3.08 ms against 1.39 ms for null). On the i5 they take 21% longer (1.49 ms against 1.23 ms). There is no earlier nas result for this row, and the branch does not touch it.

### Showcase

Against main, the primitive-array scenarios are much faster, while Apache.Avro's times are unchanged:

- **Reads:** DoubleArray −80% (i5) and −84% (nas), IntArray −88% on both, LongArray −79% and −80%. Allocation halves or better: 16,128 B becomes 8,128 B, or 4,128 B for IntArray.
- **Writes:** DoubleArray −79% and −94%, IntArray and LongArray −43% to −47%.

This matches `97d0fdb` (typed primitive arrays in the generic model), which is on main. The Int and Long array writes also go through `WriteLongs`/`WriteInts`, so part of their gain may be this branch; the runs cannot separate the two. Counters and Telemetry are within ±3% of main. BooleanArray is new, at 0.02× Apache.Avro's time for reads and 0.005–0.006× for writes.

### Unchanged

Bulk long reads, schema parsing and varint decode are within ±5% of 7311fd2. The only exceptions are the 3-byte decode swings described above. The gains of the first run hold.

## Follow-ups

1. **8-byte encode, 10-byte encode and Mixed1-10 on CPUs without fast PDEP:** stop building `low` for values that don't use it, or test for 6 bytes and up first. Then re-measure against main on both machines. This is the one clear regression the branch still carries.
2. **2-byte encode on the nas:** repeat `VarintBenchmarks` with `--filter '*Varint*'` on the nas. If +26% holds, look at the generated code for the call site, since it should be the same as main's.
3. **WideRecord generated write +5–6%:** check it after follow-up 1, since wide records have many longer values.
4. **The `FastBmi2` inline path is not measured by either run.** The EPYC 7543 or the i7-12800H would cover it.
5. **The nas's 1-byte encode gate failure** predates this branch and is unchanged.

The BenchmarkDotNet reports are not committed. They are in `BenchmarkDotNet.Artifacts` on each machine: `BenchmarkRun-20260928-160958.log` on the i5 and `BenchmarkRun-20260928-160955.log` on the nas.
