#if NET8_0_OR_GREATER
using System;
using System.Globalization;

namespace AvroSharp.IO;

/// <summary>
/// TEMPORARY (#102): selects the order of the length tests in the out-of-line varint writer for one benchmark run on
/// CPUs without fast PDEP (MultiByteOrderBenchmarks), from the AVROSHARP_MULTIBYTE_ORDER environment variable:
/// 0 the branch as it is (2^35 first, then 3, 4, 5 bytes, then the word), 1 main's order (3, 4, then the word for 5 to
/// 8), 2 3, 4, 5, then the word for 6 to 8. A static readonly field, so the JIT folds it. Remove before merging.
/// </summary>
internal static class MultiByteOrderVariant
{
    public static readonly int Value =
        int.TryParse(Environment.GetEnvironmentVariable("AVROSHARP_MULTIBYTE_ORDER"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
#endif
