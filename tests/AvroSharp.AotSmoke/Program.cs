using System;
using AvroSharp;

// Native AOT smoke test: exercises the public API from a trimmed, AOT-compiled executable.
// Grows with each milestone; the exit code is the test result.
var failures = 0;

foreach (var name in new[] { AvroCodecNames.Null, AvroCodecNames.Deflate, AvroCodecNames.Snappy, AvroCodecNames.Bzip2, AvroCodecNames.Xz, AvroCodecNames.Zstandard })
{
    if (!AvroCodecNames.IsStandard(name))
    {
        Console.Error.WriteLine($"FAIL: '{name}' not recognised as a standard codec");
        failures++;
    }
}

if (AvroCodecNames.IsStandard("zstd"))
{
    Console.Error.WriteLine("FAIL: 'zstd' recognised as a standard codec");
    failures++;
}

Console.WriteLine(failures == 0 ? "AOT smoke test passed" : $"AOT smoke test failed ({failures})");
return failures == 0 ? 0 : 1;
