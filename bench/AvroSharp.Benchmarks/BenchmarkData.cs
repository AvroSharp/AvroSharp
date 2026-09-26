using System;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Shared test data rules. Datasets are large (64K values) and random, without repeating patterns: when a small
/// dataset is replayed, the CPU's branch predictor learns it, which makes branchy code look faster than it is on
/// real data (docs/reviews/2026-09-25-performance.md, §1.2).
/// </summary>
internal static class BenchmarkData
{
    /// <summary>Values per operation for primitive encoding and decoding benchmarks.</summary>
    public const int ValueCount = 64 * 1024;

    /// <summary>
    /// A random long whose zig-zag varint encoding is exactly <paramref name="bytes"/> bytes (1 to 10), with every
    /// value in that length's range equally likely.
    /// </summary>
    public static long LongOfEncodedLength(Random random, int bytes)
    {
        var low = bytes == 1 ? 0UL : 1UL << (7 * (bytes - 1));
        var high = bytes == 10 ? ulong.MaxValue : (1UL << (7 * bytes)) - 1;
        var zigZag = low + (ulong)(random.NextDouble() * (high - low));
        return (long)(zigZag >> 1) ^ -(long)(zigZag & 1);
    }
}
