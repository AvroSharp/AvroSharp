# Benchmarks of perf/varints-parse on an i5-3570K and a Ryzen 5 3500U

Runs of the `perf/varints-parse` branch (#24, #102, #27, #103) on two machines, analyzed on 2026-09-28. Each is compared with the previous run on the same machine.

| | Desktop | nas |
|---|---|---|
| CPU | Intel Core i5-3570K (Ivy Bridge), x86-64-v2, no BMI1/BMI2/LZCNT | AMD Ryzen 5 3500U (Zen+), x86-64-v3, microcoded PDEP/PEXT (#39) |
| OS | Windows 10 22H2 (10.0.19045) | Ubuntu 24.04.5 LTS |
| Runtime | .NET 10.0.12, SDK 10.0.401 | .NET 10.0.7, SDK 10.0.203 |
| Tool | BenchmarkDotNet 0.15.8, DefaultJob | BenchmarkDotNet 0.15.8, DefaultJob |
| Code | `7311fd2`, clean tree; the run started 4 minutes after that commit | `7311fd2`, clean tree |
| Classes | BulkRead, GenericRecord, SchemaParse, Varint, WideRecord (71 benchmarks) | the same five (76 benchmarks; Varint adds the 10-byte case) |
| Previous run | `BenchmarkRun-20260927-230341.log`, main before this branch | `BenchmarkRun-20260927-173641.log`, main before this branch |

Both previous runs predate the generated-code commits that landed on main on 2026-09-28 (`7ae04ba`, `594f543`, `c536cfb`, `c36c660`). A change in a benchmark this branch does not touch can come from those.

Times are means. "Δ" is AvroSharp's time now against its time in the previous run on the same machine. The Apache.Avro baselines moved by up to 8% between runs, and on the nas more for some rows, so the comparison uses AvroSharp's absolute times rather than the ratios.

## Summary

- **Bulk long reads (#24) and schema parsing (#103) are faster on both machines**, and parsing allocates about 20% less.
- **Varint encode (#102) is faster for 5 bytes and up on both machines. It is not a win for short values, and the loss is on a different side on each machine:** 3 and 4 bytes are slower on the i5, while 2 bytes and Mixed1-2 are slower on the nas.
- **`WriteLongs` is now slower than one-at-a-time writes for 8-byte values and Mixed1-10 on both machines.**
- **The gate fails on the nas for 1-byte encode:** 217.1 µs against Apache.Avro's 198.6 µs, 1.09× slower. It failed before this branch too (1.15×).

## Varint encode (#102)

| Bytes | i5 before | i5 now | Δ | nas before | nas now | Δ |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 133.4 µs | 132.6 µs | −0.6% | 212.4 µs | 217.1 µs | +2.2% |
| 2 | 139.2 µs | 140.3 µs | +0.8% | 170.2 µs | 192.7 µs | **+13.2%** |
| 3 | 325.1 µs | 372.4 µs | **+14.5%** | 362.1 µs | 320.4 µs | −11.5% |
| 4 | 356.1 µs | 371.3 µs | **+4.3%** | 370.1 µs | 318.5 µs | −13.9% |
| 5 | 498.5 µs | 372.0 µs | −25.4% | 421.2 µs | 310.4 µs | −26.3% |
| 8 | 498.1 µs | 371.4 µs | −25.4% | 415.9 µs | 312.0 µs | −25.0% |
| 10 | not run | not run | | 380.0 µs | 411.4 µs | **+8.3%** |
| Mixed1-10 | 657.2 µs | 544.9 µs | −17.1% | 604.4 µs | 513.9 µs | −15.0% |
| Mixed1-2 | 302.5 µs | 313.9 µs | +3.8% | 288.6 µs | 330.3 µs | **+14.4%** |

The errors are under 4 µs on every row, so each change in bold is beyond noise.

- **The i5: 3 to 8 bytes now all take about 372 µs.** The inline word path costs the same for every length. This CPU has no BMI2, so `SpreadVarint` takes the shift-and-mask fallback over all eight groups, where the previous code wrote three or four bytes directly. That cause is reasoned from the code, not profiled.
- **The nas: 3 to 8 bytes gain 11–26%. The 2-byte and Mixed1-2 losses are larger than the ~5% the commit message reports for the EPYC 7543.** Most values in real records are 1 or 2 bytes (lengths, counts, small ints), so this is the side that matters for record writes. The record benchmarks that would show it (BinaryEncoding, Container, Showcase) were not run on either machine.
- **10 bytes is slower on the nas (+8.3%).** The branch does not change the 9- and 10-byte path itself, but that path now sits after the inline word check.
- The commit message leaves #102 "to be confirmed on the i7 before release". Neither machine here confirms it for every length.

### Bulk writes against one at a time

`AvroSharp_EncodeBulk` (`WriteLongs`) is new in this branch.

| Bytes | i5 Encode | i5 EncodeBulk | nas Encode | nas EncodeBulk |
|---|---:|---:|---:|---:|
| 1 | 132.6 µs | 69.9 µs | 217.1 µs | 153.3 µs |
| 2 | 140.3 µs | 88.9 µs | 192.7 µs | 159.2 µs |
| 3 | 372.4 µs | 212.1 µs | 320.4 µs | 221.4 µs |
| 4 | 371.3 µs | 209.1 µs | 318.5 µs | 221.0 µs |
| 5 | 372.0 µs | 226.4 µs | 310.4 µs | 250.6 µs |
| 8 | 371.4 µs | **489.0 µs** | 312.0 µs | **612.7 µs** |
| 10 | not run | not run | 411.4 µs | 383.5 µs |
| Mixed1-10 | 544.9 µs | **631.8 µs** | 513.9 µs | **682.1 µs** |
| Mixed1-2 | 313.9 µs | 276.3 µs | 330.3 µs | 313.3 µs |

- For 1 to 5 bytes the bulk path wins on both machines. That includes the nas's 1-byte case: 153.3 µs is faster than Apache.Avro.
- **For 8 bytes and Mixed1-10 the bulk path loses on both.** `WriteLongs` calls `WriteVarintAt`, which sends everything above 2 bytes to the out-of-line `WriteVarintMulti`. The single-value path now handles 3 to 8 bytes inline. `WriteVarintMulti` keeps a 4-byte store for 3 to 5 bytes, which is why the bulk path still wins there.
- The nas's 8-byte bulk row is bimodal (BenchmarkDotNet's `MultimodalDistribution`, mValue 3.21; StdDev 147 µs), so its mean is uncertain. The i5 row is stable (StdDev 0.8 µs) and shows the same loss.

## Bulk long reads (#24)

| Data | i5 before | i5 now | Δ | nas before | nas now | Δ |
|---|---:|---:|---:|---:|---:|---:|
| SmallValues | 79.6 µs | 40.9 µs | **−48.6%** | 71.0 µs | 25.7 µs | **−63.8%** |
| Mixed | 184.5 µs | 132.9 µs | **−27.9%** | 198.0 µs | 125.6 µs | **−36.5%** |
| Timestamps | 534.4 µs | 536.4 µs | +0.4% | 517.6 µs | 516.1 µs | −0.3% |

The scalar loop (`AvroSharpScalar_ReadLongLoop`) is unchanged on both machines, as expected. On Timestamps, `ReadLongs` is still slower than the scalar loop: 11% on the i5 (536 against 481 µs) and 3% on the nas (516 against 500 µs). That predates this branch; #24 speeds up runs of short values, not 8-byte values.

## Schema parsing (#103)

| Case | i5 before | i5 now | Δ | nas before | nas now | Δ |
|---|---:|---:|---:|---:|---:|---:|
| Parse, Large | 557.3 µs, 439.2 KB | 515.5 µs, 349.2 KB | −7.5% | 653.3 µs, 436.4 KB | 534.1 µs, 349.2 KB | −18.3% |
| Parse, Small | 10.49 µs, 8.37 KB | 9.42 µs, 7.16 KB | −10.2% | 10.86 µs, 8.32 KB | 8.70 µs, 7.16 KB | −19.9% |
| ParseAndFingerprint, Large | 703.7 µs, 511.1 KB | 617.3 µs, 421.1 KB | −12.3% | 766.2 µs, 508.3 KB | 664.0 µs, 421.1 KB | −13.3% |
| ParseAndFingerprint, Small | 12.96 µs, 9.85 KB | 12.05 µs, 8.65 KB | −7.0% | 13.48 µs, 9.80 KB | 11.30 µs, 8.65 KB | −16.2% |

- Memory drops by 12–21% for every case.
- **Parse, Large now shows Gen1 collections on the i5** (none before, 21.5 per 1,000 operations now). The nas had Gen1 collections before and has fewer now (54.7 to 35.2). What lives past Gen0 on the i5 has not been investigated.
- The small-schema margin over Apache.Avro, the thinnest parsing case in the [i7 run](2026-09-28-benchmarks.md), widens to 2.10× on the i5 and 2.50× on the nas.

## Varint decode

The branch does not change single-value decode. Most rows are within ±3% on both machines. The exceptions are 4 bytes on the i5 (−10.1%, 300.8 to 270.3 µs), and 3 and 4 bytes on the nas (−9.9%, 302.8 to 272.9 µs; −5.4%, 310.1 to 293.3 µs). They are on different lengths on each machine, and they are probably from JIT code layout. That explanation has not been checked.

## Records

| Row | i5 before | i5 now | Δ | nas before | nas now | Δ |
|---|---:|---:|---:|---:|---:|---:|
| GenericRecord `AvroSharp_Read` | 1,486.2 ns, 3,424 B | 1,143.4 ns, 2,912 B | −23.1% | 1,579.5 ns, 3,424 B | 1,246.0 ns, 2,912 B | −21.1% |
| GenericRecord `AvroSharp_Generated_Read` | 800.6 ns, 2,192 B | 781.4 ns, 2,048 B | −2.4% | 960.2 ns, 2,192 B | 887.1 ns, 2,048 B | −7.6% |
| GenericRecord `AvroSharp_Write` | 709.3 ns | 692.0 ns | −2.4% | 836.4 ns | 734.2 ns | −12.2% |
| GenericRecord `AvroSharp_Generated_Write` | 470.7 ns | 425.4 ns | −9.6% | 578.3 ns | 444.9 ns | −23.1% |

The reads allocate 512 B and 144 B less. Nothing in this branch obviously accounts for that, so it probably comes from the main commits that landed between the runs. A run of main at `c36c660` would separate the two.

`WideRecordBenchmarks` has no earlier run on either machine. AvroSharp is faster than Apache.Avro on every row: generic read 3.59× on the i5 and 4.16× on the nas, generated read 5.99× and 6.64×, generic write 1.75× and 1.73×, generated write 6.95× and 7.95×. The generic write is the thin one.

## Follow-ups

1. Decide the short-value side of #102 for each machine class: 3 and 4 bytes without BMI2, and 1 and 2 bytes on Zen+. Also check that the 9- and 10-byte path did not lose on its own.
2. Make `WriteLongs`/`WriteInts` use the same inline word path as `WriteVarint` for 6 bytes and up, so the bulk path is never slower than one value at a time.
3. Run BinaryEncoding, Container and Showcase on this branch. They show whether the encode changes reach record writes.
4. The nas's 1-byte encode gate failure predates this branch. The bulk path already beats Apache.Avro for 1-byte values; the single-value path does not.

The BenchmarkDotNet reports are not committed. They are in `BenchmarkDotNet.Artifacts` on each machine.
