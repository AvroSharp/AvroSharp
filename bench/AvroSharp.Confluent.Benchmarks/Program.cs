using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release -f net10.0 -- [BenchmarkDotNet arguments], such as --filter *Serialize*
var config = ManualConfig.Create(DefaultConfig.Instance).WithBuildTimeout(TimeSpan.FromMinutes(10));
BenchmarkSwitcher.FromAssembly(typeof(AvroSharp.Confluent.Benchmarks.SerdeBenchmarks).Assembly).Run(args, config);
