using System;
using System.Linq;
using AvroSharp.Benchmarks;
using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release -- [BenchmarkDotNet arguments] [--gate]
// With --gate, the process exits non-zero unless AvroSharp beats the Apache.Avro baseline in every group.
System.Console.WriteLine(BuildInfo.Version);
var gate = args.Contains("--gate", StringComparer.Ordinal);
var summaries = BenchmarkSwitcher
    .FromAssembly(typeof(Gate).Assembly)
    .Run(args.Where(a => !string.Equals(a, "--gate", StringComparison.Ordinal)).ToArray());

return gate ? Gate.Evaluate(summaries) : 0;
