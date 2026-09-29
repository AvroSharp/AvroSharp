# perf/varints-parse at 8164630 on CPUs without fast PDEP

The third run of the `perf/varints-parse` branch on the i5-3570K and the nas, analyzed on 2026-09-28. It measures `8164630`, which builds the 4-group value only below 2^35 and initializes `FastBmi2` in the reader and writer constructors. It is the counterpart of the [fast-PDEP run](2026-09-28-varints-parse-fast-pdep.md) of the same commit on the i7-12800H and the EPYC 7543. On both machines here `FastBmi2.IsSupported` is false, so every varint above 2 bytes takes the out-of-line `WriteVarintMulti`.

| | Desktop | nas |
|---|---|---|
| CPU | Intel Core i5-3570K (Ivy Bridge), no BMI2 | AMD Ryzen 5 3500U (Zen+), microcoded PDEP/PEXT |
| OS, runtime | Windows 10 22H2, .NET 10.0.12 | Ubuntu 24.04.5 LTS, .NET 10.0.7 |
| Job | DefaultJob | DefaultJob |
| Code | `8164630`, clean tree; the run started 18 minutes after the commit | `8164630`, clean tree |
| Size | 168 benchmarks in 59 minutes: the full suite | 173 benchmarks in 59 minutes (Varint adds the 10-byte case) |

These runs predate `88f2ed9` (the PDEP word store in the bulk loop). That commit changes only the fast-PDEP path, which neither machine takes. They also predate the temporary bulk-variant commit `ec01828`.

The comparisons are with the [second run](2026-09-28-varints-parse-rerun.md) (`41d6483`, "v2") and with the 2026-09-27 runs of main. The second run's notes on run-to-run variation apply here too. This time 3-byte decode, which no commit on the branch touches, moved −14.9% on the nas. And the i5's Apache.Avro 2- and 4-byte encode rows moved +11.5% and +8.9%, with no change to Apache.Avro.

## Summary

- **The gate passes on the i5 (106 AvroSharp rows). On the nas, 1 of 109 rows fails: 1-byte encode, now 1.50× slower than Apache.Avro,** against 1.09× at v2 and 1.15× on main. The time went from 216.8 to 294.3 µs, with a StdDev of 0.46 µs, so the row is stable within this run. Of the fast-PDEP review's follow-up 2 (the 1-byte cost), the i5 does not show it (+0.6% against main); the nas shows it, larger.
- **Fixed by 8164630:**
  - 8-byte encode is back to main's level: −4.5% (i5) and +0.6% (nas) against main.
  - 10-byte encode on the nas is 10.8% faster than main.
  - 2-byte encode on the nas is back to main's level (−0.1%). That fits the static-base helper call that `8164630` removed from the loop.
- **Still open: Mixed1-10 encode is 11% (i5) and 28% (nas) slower than main.**
- **The bulk writers are at least as fast as single writes for every length on both machines.** On the nas, Showcase Int and Long array writes are 21–27% faster than at v2.
- **Record-level encode (BinaryEncoding) is within −10% to +3% of main.** Schema parsing, bulk reads and decode hold their gains.

## Varint encode, one value at a time

| Bytes | i5 main | i5 v2 | i5 8164630 | vs main | nas main | nas v2 | nas 8164630 | vs main |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 133.4 µs | 133.3 µs | 134.3 µs | +0.6% | 212.4 µs | 216.8 µs | 294.3 µs | **+38.6%** |
| 2 | 139.2 µs | 136.4 µs | 139.9 µs | +0.5% | 170.2 µs | 215.2 µs | 170.0 µs | −0.1% |
| 3 | 325.1 µs | 287.9 µs | 303.7 µs | −6.6% | 362.1 µs | 330.6 µs | 337.1 µs | −6.9% |
| 4 | 356.1 µs | 302.6 µs | 305.6 µs | −14.2% | 370.1 µs | 328.9 µs | 336.7 µs | −9.0% |
| 5 | 498.5 µs | 327.3 µs | 333.8 µs | −33.0% | 421.2 µs | 344.3 µs | 357.1 µs | −15.2% |
| 8 | 498.1 µs | 557.9 µs | 475.6 µs | −4.5% | 415.9 µs | 488.7 µs | 418.3 µs | +0.6% |
| 10 | not run | not run | not run | | 380.0 µs | 429.6 µs | 339.0 µs | −10.8% |
| Mixed1-10 | 657.2 µs | 730.5 µs | 732.1 µs | **+11.4%** | 604.4 µs | 711.0 µs | 772.9 µs | **+27.9%** |
| Mixed1-2 | 302.5 µs | 299.2 µs | 334.5 µs | +10.6% | 288.6 µs | 299.3 µs | 289.4 µs | +0.3% |

- **1 byte on the nas:** a 1-byte value never leaves the inline fast path. The only change `8164630` makes that such a loop can reach is `FastBmi2.EnsureInitialized()` in the writer constructor, and the benchmark constructs one writer per operation of 64K values. So the cause is not known. The i5 runs the same code at +0.6% against main. The row was stable at about 217 µs in both earlier runs of the branch, and 36% is twice the largest move seen with unchanged code (17%). It is probably real, but a repeat `--filter '*Varint*'` run on the nas is needed before acting on it.
- **Mixed1-10:** every fixed length from 3 to 10 bytes is now as fast as main or faster, but the random mix is not. `WriteMultiByteVarint` now tests 2^35, then 3, 4 and 5 bytes, then 6 to 8. Main tested 3 and 4 bytes, then 5 to 8. With random lengths each extra test can mispredict. That is the same effect the fast-PDEP review found in the bulk loop. The explanation is reasoned from the code, not profiled.
- **Mixed1-2 on the i5 (+10.6%):** the 1- and 2-byte code is unchanged, the nas row is flat (+0.3%), and the i5's Apache.Avro 2-byte row moved +11.5% in the same run. This is probably run variation.

## Varint encode, bulk (`WriteLongs`)

| Bytes | i5 v2 | i5 8164630 | i5 single | nas v2 | nas 8164630 | nas single |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 73.6 µs | 71.3 µs | 134.3 µs | 153.8 µs | 154.1 µs | 294.3 µs |
| 2 | 98.5 µs | 88.6 µs | 139.9 µs | 164.1 µs | 155.4 µs | 170.0 µs |
| 3 | 159.3 µs | 169.3 µs | 303.7 µs | 164.2 µs | 187.0 µs | 337.1 µs |
| 4 | 164.1 µs | 165.0 µs | 305.6 µs | 191.0 µs | 180.5 µs | 336.7 µs |
| 5 | 179.1 µs | 174.5 µs | 333.8 µs | 203.0 µs | 220.6 µs | 357.1 µs |
| 8 | 465.5 µs | 368.0 µs | 475.6 µs | 388.3 µs | 313.4 µs | 418.3 µs |
| 10 | not run | not run | | 323.0 µs | 245.0 µs | 339.0 µs |
| Mixed1-10 | 621.8 µs | 654.5 µs | 732.1 µs | 585.9 µs | 660.5 µs | 772.9 µs |
| Mixed1-2 | 289.2 µs | 274.2 µs | 334.5 µs | 307.1 µs | 313.9 µs | 289.4 µs |

- **Bulk beats single for every length on both machines**, except the nas's Mixed1-2, where bulk is 8% slower (314 against 289 µs). At v2 the two were equal.
- 8 and 10 bytes gained 19–24% from v2, the same fix as the single rows.
- Bulk Mixed1-10 lost 5% (i5) and 13% (nas) from v2. That fits the extra length test above.

## Other benchmarks

Against v2, the non-varint rows are within ±5% on both machines, except these:

- **Showcase Int and Long array writes on the nas: −21% and −27%** (1,572 to 1,239 ns and 1,588 to 1,161 ns); BooleanArray writes −7.5%. They go through `WriteInts`/`WriteLongs`, and fit the static-base helper call that `8164630` removed from the loop. On the i5 they move +0.2% and −5.2%.
- **Container rows moved in both directions with the container code unchanged:** on the nas from −6.1% to +10.9%. The largest are Write deflate +10.9%, pipelined null reads +9.6%, and sequential null reads +8.8%. On the i5 they range from −5.5% to +6.4%. BenchmarkDotNet flagged the i5's `ContainerBenchmarks.AvroSharp_Read` as multimodal (mValue 2.85).
- **WideRecord `AvroSharp_Read` on the nas: +9.9%** (2,015 to 2,215 ns). The i5 row is +0.7%, and the read path changed only by the `FastBmi2.EnsureInitialized()` call in the reader constructor. One call per record read is a static-field check once the class is initialized, so this is probably variation. That is reasoned, not measured.
- **GenericRecord `AvroSharp_Read` on the i5: −8.7%**, back from the +11.5% of v2. The row has moved by about 10% in each direction without a read change.
- **BinaryEncoding encode on the nas:** Longs and Strings −6.3%, RealisticLongs +5.3%.

Against main:

- **BinaryEncoding encode:** i5 from −10.2% (Longs) to −0.5% (RealisticLongs); nas from −7.4% (Strings) to +2.6% (RealisticLongs). Faster than Apache.Avro by 1.25× (Strings) to 2.04× (Longs) on the i5, and 1.48× to 3.15× on the nas. The nas's Apache.Avro encode rows were multimodal in the second run, so its multipliers are less certain.
- **Resolution,** which ran for the first time on this branch: generic reads 2.01× (i5) and 2.07× (nas) faster than Apache.Avro, and generated reads 2.75× and 2.71×. The nas's generated read went from 691.8 ns on main, when it was slower than the generic read, to 432.3 ns. That is probably the generated-code commits on main, not this branch.
- **Schema parsing, bulk reads, varint decode and the container and Showcase gains** are as described in the second run.
- **Thinnest margin: bzip2 container writes,** 0.93–0.99× Apache.Avro's time. The i5's generated bzip2 write is at 0.99× (214.3 ms). Compression dominates these rows.

## Follow-ups

1. **nas 1-byte encode (the gate failure):** repeat `VarintBenchmarks` on the nas. If 294 µs holds, compare the tier-1 code of `EncodeAll` (the single-write loop) at 41d6483 and 8164630 (for example with `DOTNET_JitDisasm`).
2. **Mixed1-10 on CPUs without fast PDEP:** order the tests in `WriteMultiByteVarint` so random lengths pay no more branches than main's (3, 4, then the word), and measure Mixed1-10 on both machines.
3. **After `88f2ed9` and the `ec01828` bulk-variant run,** re-run VarintBenchmarks here too. `88f2ed9` should not change these machines, since it only touches the fast-PDEP path; that is worth confirming.

The BenchmarkDotNet reports are not committed. They are in `BenchmarkDotNet.Artifacts` on each machine: `BenchmarkRun-20260928-181500.log` on the i5 and `BenchmarkRun-20260928-181450.log` on the nas.
