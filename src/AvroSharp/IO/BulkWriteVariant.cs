#if NET8_0_OR_GREATER
using System;
using System.Globalization;

namespace AvroSharp.IO;

/// <summary>
/// TEMPORARY (#102): selects the bulk varint write candidate for one benchmark run (BulkWriteVariantBenchmarks), from
/// the AVROSHARP_BULK_VARIANT environment variable: 0 the PDEP word for 3 to 8 bytes, 1 the length-tested path, 2 a
/// 4-byte store without a jump for 3 to 5 bytes and the word for 6 to 8. A static readonly field, so the JIT folds it
/// and each benchmark process gets the code of one candidate. Remove, with the losing candidates, before merging.
/// </summary>
internal static class BulkWriteVariant
{
    public static readonly int Value =
        int.TryParse(Environment.GetEnvironmentVariable("AVROSHARP_BULK_VARIANT"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
#endif
