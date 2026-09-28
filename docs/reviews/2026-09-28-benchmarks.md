# Full benchmark run: i7-12800H, .NET 10

The first full run of the benchmark suite since the 0.1 release, with the `--gate` check. Analyzed on 2026-09-28.

| | |
|---|---|
| CPU | Intel Core i7-12800H (Alder Lake), pinned to logical core 0 (`--affinity 1`) |
| OS | Windows 11 25H2 (10.0.26200) |
| Runtime | .NET 10.0.11, x64 RyuJIT x86-64-v3 (SDK 10.0.400) |
| Tool | BenchmarkDotNet 0.15.8, `--filter '*' --memory --gate --affinity 1` |
| Code | `79ee062` on main, which is the 0.1.1 library code; the run started 4 minutes after that commit |
| Size | 154 benchmarks in 40 minutes |

**Faster** is how many times faster AvroSharp is than Apache.Avro: Apache.Avro's mean time divided by AvroSharp's. 2.00× means AvroSharp takes half the time.

## Summary

- **The gate passes.** Each of the 94 AvroSharp rows is faster than its Apache.Avro baseline, and none allocates more.
- The multipliers range from **1.04×** (bzip2 container writes) to **22.39×** (xz container reads with generated code).
- **#32's "container benchmarks beat Apache for every codec" criterion is met on x64.** Every codec is faster for both reads and writes, and in both the generic and the generated forms.

| Area | Faster |
|---|---|
| Varint decode | 1.42–3.16× |
| Varint encode | 1.09–4.52× |
| Binary encoding (longs, strings, mixed) | 1.19–1.98× |
| Bulk long reads | 2.02–3.27× |
| Schema parsing | 1.08–1.58× |
| Records, generic model | read 1.95×, write 3.93× |
| Records, generated code | read 3.95×, write 6.18× |
| Schema resolution | generic 1.32×, generated 2.72× |
| Showcase scenarios | read 1.73–4.42×, write 3.77–20.20× |
| Container reads (null, deflate, snappy, zstandard) | generic 2.90–3.72×, generated 5.98–7.36× |
| Container reads, xz | generic 10.91–11.72×, generated 22.39× |
| Container reads, bzip2 | 1.63–2.11× |
| Container writes (null, deflate, snappy, zstandard, xz) | generic 3.46–6.44×, generated 5.19–10.35× |
| Container writes, bzip2 | 1.04–1.05× |

## Where the margins are thin

1. **bzip2 container writes: 1.04–1.05×.** Compression dominates the time: 125 ms out of 131 ms. Both libraries compress with SharpZipLib, so only the Avro encoding differs. More speed here would need a faster bzip2 compressor, not Avro changes.
2. **Varint encode of 3-byte values: 1.09×.** Varint encodes of 4 and 5 bytes are 1.39× and 1.47×. Decode of 5-byte values is 1.42×. The mid-length varint paths remain the weakest spot (see [hybrid varints](2026-09-26-hybrid-varints.md)).
3. **Small schema parsing: 1.08×.** A small schema takes 7.3 µs against 7.9 µs. The large schema parses 1.58× faster, so the fixed cost of a parse is the part to look at. AvroSharp allocates about a third of Apache.Avro's memory either way.
4. **String decode and encode: 1.19× and 1.22×.** The UTF-8 transcoding is the same work in both libraries. Decoding allocates the same memory, because the strings themselves are the allocation.

## Other observations

- **AvroSharp allocates less everywhere.** Writes allocate close to nothing: under 6 KB for a whole container file, against 5.7 MB for Apache.Avro, except xz at 777 KB. Generated reads allocate the least.
- **The generated code is about twice as fast as the generic model** for records, resolution and container reads.
- **Pipelined async reads don't help on one core, which is expected.** With `--affinity 1`, decompression and decoding cannot overlap. For fast codecs they are 3–10% slower than sequential reads. bzip2 is the exception: 2.04× against 1.65×. The pipelined numbers need a run without affinity to be meaningful.
- **Async and sync container reads and writes are within a few percent of each other.**

## Caveats

- This is one machine: x64 on Windows with .NET 10. Arm64, Linux, and .NET 8 and 9 were not run.
- Pinning to one core makes the numbers stable, but it hides any parallelism: the pipelined reads, and the thread pool behind async I/O.
- The multipliers come from the means. The BenchmarkDotNet reports are in `BenchmarkDotNet.Artifacts/results`, which is not committed.

## Results

Each table shows, for every case, the method (AvroSharp's side), Apache.Avro's mean, AvroSharp's mean, how many times faster AvroSharp is, and allocated memory per operation as AvroSharp / Apache.Avro.
### Varints

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Decode, Bytes 1 | decode | 122.82 µs | 38.88 µs | **3.16×** | 0 B / 88 B |
| Decode, Bytes 10 | decode | 623.31 µs | 223.73 µs | **2.79×** | 0 B / 89 B |
| Decode, Bytes 2 | decode | 173.77 µs | 57.11 µs | **3.04×** | 0 B / 88 B |
| Decode, Bytes 3 | decode | 235.63 µs | 138.79 µs | **1.70×** | 0 B / 89 B |
| Decode, Bytes 4 | decode | 304.71 µs | 146.15 µs | **2.08×** | 0 B / 89 B |
| Decode, Bytes 5 | decode | 343.54 µs | 242.42 µs | **1.42×** | 0 B / 89 B |
| Decode, Bytes 8 | decode | 513.58 µs | 235.00 µs | **2.19×** | 0 B / 89 B |
| Decode, Bytes Mixed1-10 | decode | 984.90 µs | 523.16 µs | **1.88×** | 0 B / 89 B |
| Decode, Bytes Mixed1-2 | decode | 434.16 µs | 241.50 µs | **1.80×** | 0 B / 88 B |
| Encode, Bytes 1 | encode | 63.27 µs | 42.89 µs | **1.48×** | 0 B / 0 B |
| Encode, Bytes 10 | encode | 697.92 µs | 154.41 µs | **4.52×** | 0 B / 1 B |
| Encode, Bytes 2 | encode | 119.54 µs | 58.66 µs | **2.04×** | 0 B / 0 B |
| Encode, Bytes 3 | encode | 174.24 µs | 159.79 µs | **1.09×** | 0 B / 0 B |
| Encode, Bytes 4 | encode | 234.55 µs | 169.32 µs | **1.39×** | 0 B / 0 B |
| Encode, Bytes 5 | encode | 278.02 µs | 189.69 µs | **1.47×** | 0 B / 0 B |
| Encode, Bytes 8 | encode | 553.04 µs | 185.68 µs | **2.98×** | 0 B / 1 B |
| Encode, Bytes Mixed1-10 | encode | 855.42 µs | 411.19 µs | **2.08×** | 0 B / 0 B |
| Encode, Bytes Mixed1-2 | encode | 344.04 µs | 231.64 µs | **1.49×** | 0 B / 0 B |

### Binary encoding

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Decode, Workload Longs | decode | 983.1 µs | 528.1 µs | **1.86×** | 0 B / 88 B |
| Decode, Workload Mixed | decode | 4,581.4 µs | 3,151.3 µs | **1.45×** | 5032601 B / 7654133 B |
| Decode, Workload RealisticLongs | decode | 647.9 µs | 387.9 µs | **1.67×** | 0 B / 89 B |
| Decode, Workload Strings | decode | 2,718.4 µs | 2,275.8 µs | **1.19×** | 5032601 B / 5032688 B |
| Encode, Workload Longs | encode | 819.1 µs | 413.9 µs | **1.98×** | 0 B / 25 B |
| Encode, Workload Mixed | encode | 3,991.4 µs | 2,626.1 µs | **1.52×** | 1 B / 3612056 B |
| Encode, Workload RealisticLongs | encode | 571.4 µs | 343.8 µs | **1.66×** | 0 B / 24 B |
| Encode, Workload Strings | encode | 2,188.9 µs | 1,800.2 µs | **1.22×** | 1 B / 3612057 B |

### Bulk reads

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Data Mixed | read longs | 244.99 µs | 121.09 µs | **2.02×** | 0 B / 88 B |
| Data Mixed | scalar read long loop | 244.99 µs | 98.47 µs | **2.49×** | 0 B / 88 B |
| Data SmallValues | read longs | 120.25 µs | 36.73 µs | **3.27×** | 0 B / 88 B |
| Data SmallValues | scalar read long loop | 120.25 µs | 42.68 µs | **2.82×** | 0 B / 88 B |
| Data Timestamps | read longs | 507.82 µs | 236.19 µs | **2.15×** | 0 B / 89 B |
| Data Timestamps | scalar read long loop | 507.82 µs | 232.36 µs | **2.19×** | 0 B / 89 B |

### Schema parsing

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Parse, Schema Large | parse | 586.486 µs | 371.223 µs | **1.58×** | 439.21 KB / 1399.01 KB |
| Parse, Schema Small | parse | 7.861 µs | 7.303 µs | **1.08×** | 8.37 KB / 24.3 KB |
| ParseAndFingerprint, Schema Large | parse and fingerprint | 633.320 µs | 406.204 µs | **1.56×** | 511.07 KB / 1563.02 KB |
| ParseAndFingerprint, Schema Small | parse and fingerprint | 9.393 µs | 7.024 µs | **1.34×** | 9.85 KB / 27.81 KB |

### Records

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Read | read | 1,455.2 ns | 747.7 ns | **1.95×** | 3424 B / 5032 B |
| Read | generated read | 1,455.2 ns | 368.8 ns | **3.95×** | 2192 B / 5032 B |
| Write | write | 1,290.3 ns | 328.5 ns | **3.93×** | 0 B / 5608 B |
| Write | generated write | 1,290.3 ns | 208.7 ns | **6.18×** | 0 B / 5608 B |

### Schema resolution

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Read | read | 505.1 ns | 383.7 ns | **1.32×** | 784 B / 1632 B |
| Read | generated read | 505.1 ns | 185.4 ns | **2.72×** | 480 B / 1632 B |

### Showcase scenarios

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Read, Scenario Counters | read | 229.73 ns | 56.08 ns | **4.10×** | 312 B / 688 B |
| Read, Scenario DoubleArray | read | 7,820.50 ns | 2,777.05 ns | **2.82×** | 16128 B / 32232 B |
| Read, Scenario IntArray | read | 5,612.59 ns | 3,240.96 ns | **1.73×** | 16128 B / 32232 B |
| Read, Scenario LongArray | read | 5,518.30 ns | 3,066.80 ns | **1.80×** | 16128 B / 32232 B |
| Read, Scenario Telemetry | read | 362.92 ns | 82.14 ns | **4.42×** | 456 B / 976 B |
| Write, Scenario Counters | write | 129.88 ns | 34.44 ns | **3.77×** | 0 B / 1048 B |
| Write, Scenario DoubleArray | write | 16,107.07 ns | 797.19 ns | **20.20×** | 0 B / 64024 B |
| Write, Scenario IntArray | write | 8,583.62 ns | 1,056.97 ns | **8.12×** | 0 B / 64024 B |
| Write, Scenario LongArray | write | 8,702.40 ns | 1,044.06 ns | **8.34×** | 0 B / 64024 B |
| Write, Scenario Telemetry | write | 396.77 ns | 48.15 ns | **8.24×** | 0 B / 1560 B |

### Container files

| Case | Method | Apache.Avro | AvroSharp time | Faster | Allocated (AvroSharp / Apache) |
|---|---|---|---|---|---|
| Read, Codec bzip2 | read | 33,170.0 µs | 20,133.7 µs | **1.65×** | 25637.82 KB / 31707.77 KB |
| Read, Codec bzip2 | generated read | 33,170.0 µs | 15,686.6 µs | **2.11×** | 24433.51 KB / 31707.77 KB |
| Read, Codec bzip2 | read async | 33,170.0 µs | 20,299.2 µs | **1.63×** | 25638.38 KB / 31707.77 KB |
| Read, Codec bzip2 | read pipelined async | 33,170.0 µs | 16,223.6 µs | **2.04×** | 25648.89 KB / 31707.77 KB |
| Read, Codec deflate | read | 2,615.2 µs | 824.4 µs | **3.17×** | 3357.23 KB / 8585.08 KB |
| Read, Codec deflate | generated read | 2,615.2 µs | 437.1 µs | **5.98×** | 2153.2 KB / 8585.08 KB |
| Read, Codec deflate | read async | 2,615.2 µs | 857.6 µs | **3.05×** | 3357.45 KB / 8585.08 KB |
| Read, Codec deflate | read pipelined async | 2,615.2 µs | 900.7 µs | **2.90×** | 3360.06 KB / 8585.08 KB |
| Read, Codec null | read | 2,586.4 µs | 814.7 µs | **3.17×** | 3355.79 KB / 8021.14 KB |
| Read, Codec null | generated read | 2,586.4 µs | 402.4 µs | **6.43×** | 2151.88 KB / 8021.14 KB |
| Read, Codec null | read async | 2,586.4 µs | 817.9 µs | **3.16×** | 3356.02 KB / 8021.14 KB |
| Read, Codec null | read pipelined async | 2,586.4 µs | 859.7 µs | **3.01×** | 3358.37 KB / 8021.14 KB |
| Read, Codec snappy | read | 3,142.8 µs | 844.2 µs | **3.72×** | 3356.26 KB / 8273 KB |
| Read, Codec snappy | generated read | 3,142.8 µs | 426.8 µs | **7.36×** | 2152.22 KB / 8273 KB |
| Read, Codec snappy | read async | 3,142.8 µs | 873.1 µs | **3.60×** | 3356.48 KB / 8273 KB |
| Read, Codec snappy | read pipelined async | 3,142.8 µs | 889.0 µs | **3.54×** | 3359.08 KB / 8273 KB |
| Read, Codec xz | read | 10,735.4 µs | 918.4 µs | **11.69×** | 3440.47 KB / 13706.05 KB |
| Read, Codec xz | generated read | 10,735.4 µs | 479.4 µs | **22.39×** | 2236.43 KB / 13706.05 KB |
| Read, Codec xz | read async | 10,735.4 µs | 915.9 µs | **11.72×** | 3440.69 KB / 13706.05 KB |
| Read, Codec xz | read pipelined async | 10,735.4 µs | 984.4 µs | **10.91×** | 3443.29 KB / 13706.05 KB |
| Read, Codec zstandard | read | 2,681.1 µs | 809.0 µs | **3.31×** | 3355.87 KB / 8584.18 KB |
| Read, Codec zstandard | generated read | 2,681.1 µs | 412.9 µs | **6.49×** | 2151.84 KB / 8584.18 KB |
| Read, Codec zstandard | read async | 2,681.1 µs | 837.0 µs | **3.20×** | 3356.09 KB / 8584.18 KB |
| Read, Codec zstandard | read pipelined async | 2,681.1 µs | 855.2 µs | **3.14×** | 3358.7 KB / 8584.18 KB |
| Write, Codec bzip2 | write | 131,395.2 µs | 124,848.9 µs | **1.05×** | 66915.67 KB / 72651.44 KB |
| Write, Codec bzip2 | generated write | 131,395.2 µs | 126,538.4 µs | **1.04×** | 66913.65 KB / 72651.44 KB |
| Write, Codec bzip2 | write async | 131,395.2 µs | 125,161.6 µs | **1.05×** | 66912.67 KB / 72651.44 KB |
| Write, Codec deflate | write | 1,687.4 µs | 487.3 µs | **3.46×** | 5.9 KB / 5721.89 KB |
| Write, Codec deflate | generated write | 1,687.4 µs | 325.4 µs | **5.19×** | 5.77 KB / 5721.89 KB |
| Write, Codec deflate | write async | 1,687.4 µs | 488.1 µs | **3.46×** | 5.9 KB / 5721.89 KB |
| Write, Codec null | write | 1,597.3 µs | 372.7 µs | **4.29×** | 4.7 KB / 5784.23 KB |
| Write, Codec null | generated write | 1,597.3 µs | 224.0 µs | **7.13×** | 4.6 KB / 5784.23 KB |
| Write, Codec null | write async | 1,597.3 µs | 387.8 µs | **4.12×** | 4.8 KB / 5784.23 KB |
| Write, Codec snappy | write | 2,048.5 µs | 405.8 µs | **5.05×** | 4.97 KB / 6347.28 KB |
| Write, Codec snappy | generated write | 2,048.5 µs | 245.1 µs | **8.36×** | 4.9 KB / 6347.28 KB |
| Write, Codec snappy | write async | 2,048.5 µs | 411.7 µs | **4.98×** | 5.03 KB / 6347.28 KB |
| Write, Codec xz | write | 11,675.7 µs | 1,973.3 µs | **5.92×** | 777.24 KB / 10849.34 KB |
| Write, Codec xz | generated write | 11,675.7 µs | 1,746.6 µs | **6.68×** | 777.31 KB / 10849.34 KB |
| Write, Codec xz | write async | 11,675.7 µs | 2,009.4 µs | **5.81×** | 777.31 KB / 10849.34 KB |
| Write, Codec zstandard | write | 2,828.8 µs | 452.4 µs | **6.25×** | 4.73 KB / 5721.35 KB |
| Write, Codec zstandard | generated write | 2,828.8 µs | 273.2 µs | **10.35×** | 4.67 KB / 5721.35 KB |
| Write, Codec zstandard | write async | 2,828.8 µs | 439.0 µs | **6.44×** | 4.8 KB / 5721.35 KB |
