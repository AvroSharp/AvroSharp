# perf/varints-parse at 8164630 on CPUs with fast PDEP

Full runs of the `perf/varints-parse` branch at `8164630` on two machines with fast PDEP, where `FastBmi2.IsSupported` is true and 3- to 8-byte varints take the inline word path. The i5-3570K and Ryzen 5 3500U runs ([first](2026-09-28-varints-parse.md), [second](2026-09-28-varints-parse-rerun.md)) cover the other path. Analyzed on 2026-09-28.

| | Laptop | Server |
|---|---|---|
| CPU | Intel Core i7-12800H (Alder Lake) | AMD EPYC 7543 (Zen 3), 2 sockets |
| OS, runtime | Windows 11 25H2, .NET 10.0.11 | Ubuntu 22.04.5 LTS, .NET 10.0.12 |
| Job | ShortRun (3 iterations), no affinity | ShortRun (3 iterations), no affinity |
| Size | full suite | 173 benchmarks in 20 minutes |
| Earlier result | [i7 run of main](2026-09-28-benchmarks.md), DefaultJob | EPYC runs of main (`c36c660`) earlier that day, DefaultJob, one core |

ShortRun takes three measured iterations, so single rows are less certain than in the DefaultJob runs they are compared with. The server also runs other services. Ratios to Apache.Avro, measured in the same run, are the firmer numbers.

## Summary

- **The gate passes on both machines:** every AvroSharp row is faster than its Apache.Avro baseline.
- **#102 is met on the i7:** 3-byte encode is 1.52× faster than Apache.Avro (1.09× on main), 4 bytes 2.05× and 5 bytes 2.39×.
- **Varint encode of 3 to 8 bytes is 25–38% faster than main on both machines. 1-byte encode is 6–8% slower,** which is the cost of the larger inline code seen in the EPYC A/B runs.
- **Bulk `WriteLongs` was slower than single writes for Mixed1-10 on both machines, and for 8 bytes on the i7.** Fixed in the next commit (below).
- **Schema parsing (#103):** the small schema is 2.07× faster than Apache.Avro on the i7 (1.08× on main) and 2.14× on the EPYC.
- **Bulk reads (#24) meet the SIMD rule of #29 on both machines:** mixed data is 1.48× faster than the plain loop, and timestamps are within the 3% noise band (+2.7% on the i7, −3.4% on the EPYC).

## Varint encode

| Bytes | i7 main | i7 now | Δ | vs Apache now | EPYC main | EPYC now | Δ |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 42.89 µs | 51.06 µs | +19% (ratio −8%) | 1.37× | 89.79 µs | 95.03 µs | +5.8% |
| 2 | 58.66 µs | 59.84 µs | +2.0% | 1.99× | 87.65 µs | 93.72 µs | +6.9% |
| 3 | 159.79 µs | 114.64 µs | −28% | 1.52× | 248.59 µs | 184.66 µs | −26% |
| 4 | 169.32 µs | 114.38 µs | −32% | 2.05× | 248.66 µs | 185.40 µs | −25% |
| 5 | 189.69 µs | 117.55 µs | −38% | 2.39× | 284.47 µs | 184.47 µs | −35% |
| 8 | 185.68 µs | 115.07 µs | −38% | 4.69× | 284.00 µs | 183.74 µs | −35% |
| 10 | 154.41 µs | 132.73 µs | −14% | 4.91× | 215.33 µs | 199.28 µs | −7.4% |
| Mixed1-10 | 411.19 µs | 285.70 µs | −31% | 2.90× | 464.70 µs | 372.07 µs | −20% |
| Mixed1-2 | 231.64 µs | 228.06 µs | −1.5% | 1.49× | 277.62 µs | 252.90 µs | −8.9% |

The i7's Apache.Avro 1-byte time also moved, from 63.27 to 69.94 µs, so its 1-byte row is compared by ratio as well: 1.48× on main, 1.37× now.

## Bulk writes against single writes

| Bytes | i7 single | i7 bulk | EPYC single | EPYC bulk |
|---|---:|---:|---:|---:|
| 1 | 51.06 µs | 39.73 µs | 95.03 µs | 77.32 µs |
| 2 | 59.84 µs | 48.38 µs | 93.72 µs | 75.94 µs |
| 3–5 | 114.4–117.6 µs | 82.0–89.7 µs | 184.5–185.4 µs | 117.8–130.4 µs |
| 8 | 115.07 µs | **123.92 µs** | 183.74 µs | 178.99 µs |
| Mixed1-10 | 285.70 µs | **455.69 µs** | 372.07 µs | **506.11 µs** |
| Mixed1-2 | 228.06 µs | 225.36 µs | 252.90 µs | 257.39 µs |

With fast PDEP, a single write stores 3 to 8 bytes with one word, without testing the length. The bulk loop still went through `WriteMultiByteVarint`'s 3-, 4-, 5- and 6-to-8-byte branches, which mispredict when lengths are random. The next commit makes the bulk loop use the same word store (`WriteSpreadWord`) where PDEP is fast. CPUs without it keep `WriteMultiByteVarint`, where bulk already beat single writes (the second i5 and nas run).

## Records, parsing and reads

- **Records.** Against the EPYC A/B runs of main: generated writes are 5–9% faster (GenericRecord 334 to 305 ns, WideRecord 557 to 529 ns). The generic GenericRecord write is 7% slower (498 to 535 ns). That row was the same as main in the A/B runs, which used DefaultJob and one core, so it needs a repeat before acting on it.
- **BinaryEncoding encode** is 1.19× (strings) to 2.19× (realistic longs) faster than Apache.Avro on the i7, and 1.35× to 2.23× on the EPYC.
- **Schema parsing:** Parse Small takes 4.01 µs on the i7 (7.30 µs on main) and 5.78 µs on the EPYC (7.88 µs), with 7.16 KB allocated instead of 8.37 KB.
- **Bulk reads:** SmallValues 16.9 against 45.1 µs for the plain loop on the i7, and 14.8 against 53.8 µs on the EPYC.

## Follow-ups

1. Re-run `VarintBenchmarks` encode on a fast-PDEP machine after the bulk fix: the bulk Mixed1-10 and 8-byte rows should now be faster than single writes.
2. The 1-byte cost (6–8%) comes with the inline word path. Record writes did not show it in the EPYC A/B runs; the repeat runs on the i5 and nas will show whether it holds for the other path.
3. Repeat GenericRecord `AvroSharp_Write` with DefaultJob before treating its +7% as real.
