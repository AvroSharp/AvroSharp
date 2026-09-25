using BenchmarkDotNet.Attributes;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Placeholder that keeps the benchmark pipeline exercised until the M1 schema benchmarks
/// (and the Apache.Avro comparisons) land.
/// </summary>
[MemoryDiagnoser]
public class CodecNameBenchmarks
{
    [Params("zstandard", "lz4")]
    public string Name { get; set; } = "";

    [Benchmark]
    public bool IsStandard() => AvroCodecNames.IsStandard(Name);
}
