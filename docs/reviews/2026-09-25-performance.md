# Performance review findings: encoding and decoding

Two read-only reviews of branch `m2-generic-records`, done on 2026-09-25:

- **Binary primitives**: `AvroReader`, `AvroWriter`, `PooledBufferWriter`.
- **Generic data model**: `GenericDatumReader`, `GenericDatumWriter`, `AvroValue`, `GenericRecord`.

The numbers come from scratchpad benchmarks (BenchmarkDotNet and Stopwatch) on an i7-12800H with .NET 10 and TieredPGO. They show direction only. Re-measure with the gated benchmark suite before acting on any of them.

Target frameworks are `net10.0;net9.0;net8.0;netstandard2.1;netstandard2.0`, and net8+ is the fast path. `Unsafe` and `MemoryMarshal` work on every target. `Vector128`, `Bmi2`, `BitOperations`, `Utf8.*` and `IndexOfAnyExceptInRange` need net8+.

---

## 0. Fix first: security and robustness

These three are confirmed in the code. **Status: all three fixed in PR #4** (tests in `tests/AvroSharp.Tests/Generic/HostileInputTests.cs`).

1. **A few bytes of input can force a huge allocation.** `GenericDatumReader.cs:240` sets `list.Capacity` to the block count before it reads any items. `RecordNode.MinimumSize` is `0` (`:189`), so every `array<record>` gets the zero-size cap `MaxZeroSizeItemsPerBlock = 1 << 24` (`:27`). At 16 bytes per `AvroValue`, a 5-byte input can trigger a 256 MB allocation on the large object heap. A zero-size item such as `NullNode` consumes no bytes, so repeating blocks grow memory without limit.
   - Compute a real minimum size for a record once its fields are built. A recursive reference counts as 0, so cycles are safe.
   - When `MinimumSize == 0`, pre-allocate at most `Math.Min(n, 1024)` items.
   - Add a per-read budget for zero-size items.
   - **Fixed:** records compute their minimum size from their fields; arrays and maps pre-allocate at most 1,024 items; zero-size items draw from a per-read budget (`GenericDatumReaderOptions.MaxZeroSizeItems`, default 65,536).
2. **Deeply nested input can crash the process.** Nothing limits how deep a recursive schema can nest while reading or writing. A schema like `Node{children: array<Node>}` needs about 2 bytes per level, so a crafted input of about 20–50 KB overflows the stack, and a stack overflow cannot be caught. Add an explicit depth counter (default 64–128) and pass it through `Read`/`Write`.
   - **Fixed:** `GenericDatumReaderOptions.MaxDepth` and `GenericDatumWriterOptions.MaxDepth` (default 128); the writer limit also stops a record that contains itself.
3. **An overflow throws the wrong exception.** `list.Count + n` (`:240`) can overflow `int` across blocks. It then throws `ArgumentOutOfRangeException` instead of `AvroDataException`.
   - **Fixed:** the running item count is checked against the largest .NET array length before each block, as a `long`.

---

## 1. Binary primitives (`AvroReader` / `AvroWriter`)

### 1.1 Branchless PEXT/PDEP varint paths (biggest win on realistic data)
`AvroReader.cs:446-653`, `AvroWriter.cs:247-326`. Today a chain of length checks (1, 2, 3/4, 5-8 and 9/10 bytes) handles each varint. That is fast when value lengths are predictable and slow when they are not.

Time per value, in ns, measured over 200,000 values:

| Data | Decode, current | Decode, 1-byte check then PEXT | Decode, fully branchless | Encode, current | Encode, branchless PDEP |
|---|---|---|---|---|---|
| All 1-byte | 0.75 | 0.67 | 2.87 | 0.70 | 1.73 |
| All 3-byte | 2.24 | 3.25 | 2.82 | 1.70 | 1.67 |
| All 8-byte | 3.96 | 2.96 | 2.64 | 3.00 | 1.70 |
| Random 1–8 bytes | **7.9** | 3.64 | **2.79** | **5.8** | **1.73** |
| Random 1–2 bytes | 4.06 | 4.9 | 2.71 | 3.95 | 1.74 |

Recommendation:
- Keep the inline `< 0x80` fast path.
- Replace the 2 to 8-byte checks with one branchless path, used when `Bmi2.X64.IsSupported` and at least 10 bytes are available.
- Fall back to the existing `CompactVarint`/`SpreadVarint`, which is nearly as fast.

```csharp
// decode
ref var p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_span), (nint)(uint)position);
ulong first = p; if (first < 0x80) { _position = position + 1; return first; }
var word = Unsafe.ReadUnaligned<ulong>(ref p);
var stops = ~word & 0x8080808080808080UL;
if (stops != 0) {
    var keep = ((stops & (0UL - stops)) << 1) - 1;            // bytes through the terminator
    _position = position + (BitOperations.TrailingZeroCount(stops) >> 3) + 1;
    return Bmi2.X64.ParallelBitExtract(word & keep, 0x7F7F7F7F7F7F7F7FUL);
}
// 9/10-byte: existing code

// encode (value < 2^56)
// bits = 64 - Lzcnt(value | 1); length = (bits + 6) / 7;
// word = Pdep(value, 0x7F7F..) | (0x8080.. & ((1UL << ((length - 1) * 8)) - 1));
// WriteUnaligned(word); _buffered += length;
```

- **Effect:** 2–3× faster on streams of mixed lengths, 1.3–1.5× on 8-byte values such as timestamps, no change on 1-byte values, and about 30% slower on uniform 3-byte values.
- **Risk:** PEXT and PDEP are microcoded on AMD Zen 1/2 (about 18 cycles), and .NET cannot detect those CPUs. Either accept the cost, or use the shift-and-mask fallback everywhere.

### 1.2 The benchmarks let the CPU memorize the data
`VarintBenchmarks.cs:21,50-56` uses 1,000 values of one length. `BinaryEncodingBenchmarks.cs:51-56` repeats `i % 20` and `BulkReadBenchmarks.cs:34` repeats `i % 10`. When the same 1,000 values are replayed, the branch predictor learns them, even random ones: random 1–8-byte data costs 2.9 ns/value with 1,000 values and 7.9 ns/value with 200,000.

The PERF comments that chose branchy code (`AvroReader.cs:479-485`, `AvroWriter.cs:277-284`) were based on this memorized data.
- Use at least 64K values, or a new random dataset per iteration.
- Add a workload with truly mixed lengths.
- Benchmark through a `ref AvroReader` parameter. A local reader gets promoted to registers, which hides the cost of storing and reloading its position field.
- **Fixed in the benchmark-fixes PR:** VarintBenchmarks, BinaryEncodingBenchmarks and BulkReadBenchmarks use 64K random values with no repeating pattern, VarintBenchmarks adds Mixed1-10 and Mixed1-2, and AvroSharp is called through non-inlined `ref` helpers. The PERF comments are marked for re-measurement.

### 1.3 Decode one-byte runs with SIMD, not just detect them (net8+, `Vector128`)
`AvroReader.cs:322-335, 365-378`. `OneByteRunLength` already uses SIMD to find a run of 1-byte values, but decoding the run is a scalar loop of 16 with two bounds checks per element. Decode all 16 bytes at once instead and store all 16 results unconditionally. This is safe because `destination.Length - i >= 16` is already checked, and every slot past the run is overwritten later.

```csharp
var v = Vector128.Create(chunk);
var z = ((v >> 1) ^ (Vector128<byte>.Zero - (v & Vector128.Create((byte)1)))).AsSByte(); // zigzag, fits sbyte
// widen sbyte -> short -> int (-> long); store 4 x Vector128<int> or 8 x Vector128<long> at destination[i]
_position += run; i += run;
```

- **Effect:** about 2–4× faster bulk reads of small values.
- **Remaining weakness:** data that alternates short and long values pays a 16-byte load and mask per 1-byte value. Masked VByte would fix that but is a large, table-driven change. Defer it until mixed arrays show up in the benchmarks.
- **Arm64:** `ExtractMostSignificantBits` takes 6–8 instructions there, so re-measure on Arm64.

### 1.4 Code that stops the JIT from inlining (cheap, helps broadly)
- `EnsureRemaining` (`AvroReader.cs:683-689`) and `ReadLength` (`:671-681`) build interpolated error strings inline, so the JIT does not inline them. Move the throws into `[DoesNotReturn][MethodImpl(NoInlining)] static` helpers. Do the same in `ReadBoolean` (`:97-102`).
- `ReadFloat`/`ReadDouble` (`:123-150`) contain `stackalloc`, and the JIT never inlines a method that uses `stackalloc`. Move the multi-segment path into a `NoInlining` slow method. The `try/finally` in `ReadString`'s pooled path has the same problem.
- The fast paths still carry redundant bounds checks:
  - `_buffer[_buffered++]` after `Ensure` (`AvroWriter.cs:90,259,266-267`).
  - The slice plus `BinaryPrimitives` in float/double reads and writes. Use `Unsafe.ReadUnaligned`/`WriteUnaligned` guarded by `BitConverter.IsLittleEndian`.
  - `TryReadThreeOrFourByteVarint` (`:491-503`) does 4 bounds checks. Slice once so the constant-index checks fold away.
- **Effect:** about 1–2 ns per primitive, on all targets.
- `[SkipLocalsInit]` would require `AllowUnsafeBlocks` for a negligible gain, so skip it.

### 1.5 `WriteString` in one transcoding pass (net8+)
`AvroWriter.cs:139-153` calls `GetByteCount` and then `GetBytes`.
- For `value.Length <= 21`, the UTF-8 length is at most 63, so the length prefix is always one byte. Call `Ensure(1 + 3*len)`, then `Utf8.FromUtf16(value, buf[1..], ...)`, then write the prefix.
- For longer strings, reserve the worst-case prefix, transcode, and move the bytes down if the prefix turns out shorter.
- **Effect:** about 15–25% faster short-string encoding.
- The read side (`Encoding.UTF8.GetString`) is already optimal.

### 1.6 Bulk paths that don't exist yet
- **Booleans:** `ReadBooleans(Span<bool>)` validates with `IndexOfAnyExceptInRange((byte)0, (byte)1)` and then does one memcpy. `WriteBooleans` is `WriteRaw(MemoryMarshal.AsBytes(values))`. About 10× faster.
- **`WriteLongs`/`WriteInts`:** check 8 values at a time with SIMD. If all are in [-64, 63], zigzag and narrow them into one 8-byte store; otherwise use the PDEP path.
- **Float/double arrays** are already a single memcpy on little-endian.

### 1.7 Buffer management (mostly fine)
- `PooledBufferWriter` doubles on growth and returns the whole free tail. That is good.
- `AvroWriter.cs:251-255, 371-381`: when fewer than 10 bytes remain, every varint takes the slow `WriteVarintExact` path. With an `IBufferWriter` destination, grow the buffer and retake the fast path instead.
- `WriteBytes` (`:123-127`) checks capacity twice. For small values, one `Ensure(10 + value.Length)` is enough.
- `WriteString` needs one contiguous span, which contradicts the comment at `:415` that says large values never need one huge span.

### 1.8 Already optimal, or not worth doing
- Already optimal: the inline 1-byte path, the `CompactVarint`/`SpreadVarint` bit tricks, float/double arrays as memcpy, zero-copy `ReadBytesSpan`, and the slow paths already split out.
- A fully branchless decode with no 1-byte check is 4× slower on all-1-byte streams.
- Vector256/512 and Masked VByte don't pay off for scalar fields, which are decoded one at a time.
- No correctness bugs were found in the primitives. Checked: bounds and lengths, truncated input, overlong varints, the 5th/10th-byte rules, and the invariants of the run path.

---

## 2. Generic data model

**Already done well:**
- Each schema is compiled once into sealed nodes and cached per schema.
- Union writes of primitives are resolved in O(1).
- `AvroValue` is 16 bytes.
- Bulk int/long/float/double reads are correct across multiple blocks.
- The in-place fill with `CollectionsMarshal.SetCount` is correct.

Measured: dispatch per field costs only about 10% of a read, so turning the node tree into a flat instruction list would not gain much on reads.

### 2.1 Store primitive arrays as typed arrays (largest win)
`GenericDatumReader.cs:232-267, 289-363`, `GenericDatumWriter.cs:217-230`.
- **Cost today:** decoding 1,000 doubles takes 366 ns, but storing them as `AvroValue` brings the total to 2,917 ns and 16 KB. About 0.65 ns per element is the GC write barrier. Writing takes 2,158 ns, spent on interface indexer calls and virtual dispatch.
- **Proposal:** add a `PrimitiveList<T> : IReadOnlyList<AvroValue>` backed by a `T[]`. It creates `AvroValue`s only when accessed.
  - The reader decodes blocks straight into the typed array.
  - The writer checks for `PrimitiveList<double>` and emits one memcpy.
  - Expose `AvroValue.FromArray(double[])` and similar factories so producers get the fast write path too.
- **Effect:** double arrays read about 6× and write about 7× faster, with half the allocation. Int and long arrays get 2–4×.
- **Risk:** `AvroValue.cs:25-27` promises `List<T>` instances. Before 1.0, document `IReadOnlyList<AvroValue>` as the only contract.

### 2.2 Write arrays and maps without interface calls
`GenericDatumWriter.cs:219-226`. Write from `CollectionsMarshal.AsSpan(list)` or from `AvroValue[]` directly. Writing a double array is 1.9× faster this way; copying the values into a `double[]` and calling `WriteDoubles` is 2.2× faster.

`value.Kind` is evaluated twice per array, and each evaluation checks types one by one, ending in an interface cast. Test `value.Reference` directly instead.

### 2.3 Union writes of named types do a dictionary lookup per value
`GenericDatumWriter.cs:288-294, 319`. Every record, enum or fixed inside a union hashes its full name on every write. Compare against the 1–3 named branches with `ReferenceEquals` first, and fall back to the name lookup. A `[null, Inner]` write goes from 54.7 ns to about 13 ns.

### 2.4 Record field loop
- **Write** (`GenericDatumWriter.cs:177-196`): 86 → 66 ns with a flattened loop.
  - Check `ReferenceEquals(record.Schema, schema)` before comparing names.
  - Add an internal `FieldCount` to avoid interface `Count` calls.
  - Move the per-field `try/catch` outside the loop.
  - Then optionally handle primitive fields inline with op-codes.
- **Read** (`:191-201`): about 10% faster with a `ReadInto(ref AvroValue slot)` shape, which avoids copying through a hidden return buffer.
- `GenericRecord.cs:19` makes an interface call to get the field count on every read. Add an internal constructor that takes a cached count.
- Fusing runs of fields showed no gain at this layer. Fusion belongs in the source-generated path.

### 2.5 Allocations
- `GenericDatumWriter.cs:241`: `foreach` over `IReadOnlyDictionary` boxes the enumerator, costing 64 B per map write. Special-case `Dictionary<string, AvroValue>`.
- `GenericDatumReader.cs:429`: create the map with a capacity of `Math.Min(count, 256)` taken from the first block.
- Map keys and other repeated strings could use an optional lock-free cache from UTF-8 bytes to `string`. Enum symbols already don't allocate.
- `FixedNode.Read` allocates a `byte[]` per fixed. This can't be avoided while `GenericFixed` owns a `byte[]`.
- A 24-byte `AvroValue` layout would halve the cost of storing an element but use 50% more memory. Not recommended, because §2.1 removes most of that cost.

### 2.6 Remaining gaps in the bulk paths
- **Boolean arrays** go through the virtual call per item. Read them with `ReadFixedSpan(n)` and validate with `Vector128`.
- **Double/float bulk reads** rent a buffer and then copy twice. `MemoryMarshal.Cast<byte,double>(reader.ReadFixedSpan(n*8))` is 20% faster; process 1,024 items at a time.

### 2.7 Skipping
There is no skip path yet. The writer never emits size-prefixed blocks, although `design.md` §4.3 says it does.
- Don't size blocks in the generic writer; it would need a pre-pass or double buffering.
- Add `Skip(ref AvroReader)` to each reader node for M3 projection:
  - Arrays of fixed-width items skip `n * width` bytes.
  - Size-prefixed blocks use their byte size.
  - Everything else walks the items.
- Update `design.md` to match.

### 2.8 JIT notes
- The primitive writer nodes should compare `ReferenceEquals(value.Reference, PrimitiveMarker.X)` instead of calling `Kind`, which checks up to seven types.
- In `Kind`, check exact types (`List<AvroValue>`, `AvroValue[]`, `Dictionary<string,AvroValue>`) before the interface check.

---

## Suggested order

1. The security fixes in §0.
2. The inlining fixes (§1.4), union matching by identity (§2.3) and span-based array writes (§2.2). These are cheap and safe.
3. Fix the benchmarks (§1.2), then re-measure and adopt PEXT/PDEP (§1.1).
4. Typed primitive arrays (§2.1).
5. SIMD one-byte runs (§1.3), single-pass `WriteString` (§1.5), and bulk booleans (§1.6, §2.6).
6. Skip paths for projection (§2.7), together with M3.
