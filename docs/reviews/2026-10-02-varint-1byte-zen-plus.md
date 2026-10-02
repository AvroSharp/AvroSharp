# 1-byte varint encode on Zen+ (#168)

The gate failed one row on the NAS (AMD Ryzen 5 3500U, Zen+, a 15 W low-power mini PC): `VarintBenchmarks.AvroSharp_Encode` with 1-byte values on .NET 10, a near-tie with Apache.Avro that AvroSharp lost in some processes. This review re-measures it after the NAS moved to a 7.0 kernel and Ubuntu 26.04, finds the cause, and measures a smaller 1-byte fast path (`perf/168-varint-1byte`).

**Summary**
- **The row is a tie at a hardware floor, not a regression.** A single-value write goes through `ref AvroWriter`, so each value reads `_buffered` from memory and stores it back, and the next value waits for that store to forward. Zen+ has no memory renaming for it (Zen 2 added it), so that round trip alone costs about 8.2–9.1 cycles per value. Apache.Avro's `MemoryStream` has the same chain on `_position`.
- **On .NET 10, Apache.Avro has a fast mode near the floor,** in some processes: about 9.6–10.2 cycles per value, or 183–202 µs in the gate, against 14.6–22 cycles (300–330 µs) otherwise. On .NET 8 and 9 it never ran in that mode, and AvroSharp won every process.
- **AvroSharp is also bimodal,** with the same code: about 164, 177–182, 195, 209 or 291 µs per process. Code placement causes it, not the clock: AvroSharp's half of each process ran at a nearly constant 3.08–3.25 GHz while its time moved between those modes, and with loop alignment off (`DOTNET_JitAlignLoops=0`) it was almost always 209 µs.
- **The branch narrows AvroSharp's distribution.** It removes the 209 and 291 µs modes, and in cycles its worst process is 10.2, against 11.4 (.NET 10) and 15.6 (.NET 8) on main. It can't make AvroSharp win every process against Apache.Avro's .NET 10 fast mode: the most either side can gain over the floor is a few percent, less than code placement moves a process.

## Measurements on the NAS

**Gate benchmark, 5 processes per configuration and runtime,** run interleaved (main, branch, main with loop alignment off) so drift hits all three. AvroSharp / Apache.Avro, µs:

| Runtime | Config | AvroSharp wins | Processes |
|---|---|---|---|
| .NET 10 | main | 2/5 | 179/308, 291/183, 209/202, 179/199, 211/185 |
| .NET 10 | branch | 2/5 | 195/197, 177/307, 196/185, 197/184, 196/185 |
| .NET 10 | alignment off | 1/5 | 209/185, 209/199, 211/201, 209/196, 293/311 |
| .NET 9 | all | 15/15 | AvroSharp 164–292, Apache.Avro 300–466 |
| .NET 8 | all | 15/15 | AvroSharp 164–210, Apache.Avro 328–333 |

**Cycles per value, from a harness** that runs the two in one process, interleaved in samples of about 1 ms, each timed against a chain of dependent 64-bit multiplies (3 cycles each on Zen+), 10 processes per build and runtime:

| | .NET 10 | .NET 9 | .NET 8 |
|---|---|---|---|
| AvroSharp main | 9.18–11.36 (median 11.27) | 9.33–10.10 | 9.23–15.55 |
| AvroSharp branch | 8.88–10.24 (median 9.88) | 8.63–10.05 | 8.85–10.19 |
| Apache.Avro | 9.18–15.36 | 14.94–16.64 | 14.19–15.47 |
| The bare chain (the floor) | 8.6–9.1 | | 8.2–9.0 |

The bare chain is only what every single-value write has: read the position through a `ref` to a struct, store a byte at it, and store the position back.

**The JIT's code (Tier 1, on the NAS).**
- **main** (63 bytes) has one taken branch per value. Per value, it reads `_buffer.Length` and `_buffered` for the capacity check, reads `_buffered` again with the buffer pointer, stores the byte, then increments `_buffered` in memory (a read-modify-write).
- **The branch** (61 bytes) reads `_buffered` once, checks the value and the length, stores the byte, and stores `_buffered + 1`. In exchange, it reloads the values array's length on every value, because the byte store might alias it.

## Measurements on the i7 and the EPYC

To be added: every `VarintBenchmarks` row, main against the branch. #168 allows no other row to move by more than 3%.
