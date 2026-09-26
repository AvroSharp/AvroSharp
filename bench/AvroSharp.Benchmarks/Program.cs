using System;
using System.Linq;
using AvroSharp.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release -- [BenchmarkDotNet arguments] [--gate]
// With --gate, the process exits non-zero unless AvroSharp beats the Apache.Avro baseline in every group.
System.Console.WriteLine(BuildInfo.Version);
var gate = args.Contains("--gate", StringComparer.Ordinal);

// BenchmarkDotNet builds a project per job; the default 2-minute build timeout is too short for this solution
// (analyzers and the source generator) on slower machines. When configs are merged, the longer build timeout wins,
// so a larger --buildTimeout on the command line still takes effect.
var config = ManualConfig.Create(DefaultConfig.Instance).WithBuildTimeout(TimeSpan.FromMinutes(10));
var summaries = BenchmarkSwitcher
    .FromAssembly(typeof(Gate).Assembly)
    .Run(args.Where(a => !string.Equals(a, "--gate", StringComparison.Ordinal)).ToArray(), config);

return gate ? Gate.Evaluate(summaries) : 0;
