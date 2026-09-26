using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BenchmarkDotNet.Reports;

namespace AvroSharp.Benchmarks;

/// <summary>
/// The performance gate: within each benchmark group (class, category, parameters and runtime), every
/// <c>AvroSharp_*</c> benchmark must be faster than the Apache.Avro baseline and allocate no more.
/// </summary>
internal static class Gate
{
    /// <summary>Marks a benchmark that is reported but not gated (for example Chr.Avro).</summary>
    public const string ReferenceOnly = "ReferenceOnly";

    public static int Evaluate(IEnumerable<Summary> summaries)
    {
        var failures = 0;
        var checkedCount = 0;
        foreach (var summary in summaries)
        {
            var groups = summary.Reports
                .Where(r => r.ResultStatistics is not null)
                .GroupBy(r => string.Join(
                    "|",
                    r.BenchmarkCase.Descriptor.Type.Name,
                    string.Join(",", r.BenchmarkCase.Descriptor.Categories.Where(c => !string.Equals(c, ReferenceOnly, StringComparison.Ordinal))),
                    r.BenchmarkCase.Parameters.DisplayInfo,
                    r.BenchmarkCase.Job.Environment.Runtime?.Name ?? "default"),
                    StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var baseline = group.SingleOrDefault(r => r.BenchmarkCase.Descriptor.Baseline);
                if (baseline is null)
                {
                    continue;
                }

                foreach (var candidate in group.Where(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name.StartsWith("AvroSharp_", StringComparison.Ordinal)))
                {
                    checkedCount++;
                    failures += Compare(group.Key, candidate, baseline) ? 0 : 1;
                }

                // Reference implementations such as AvroSharpScalar_* are reported next to the baseline but not gated.
                foreach (var reference in group.Where(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name.StartsWith("AvroSharpScalar_", StringComparison.Ordinal)))
                {
                    Report("INFO", group.Key, reference, baseline);
                }
            }
        }

        Console.WriteLine(checkedCount == 0
            ? "GATE FAILED: no AvroSharp benchmark was compared against a baseline."
            : $"Gate: {checkedCount - failures}/{checkedCount} comparisons passed ({BuildInfo.Version}).");
        return checkedCount == 0 || failures > 0 ? 1 : 0;
    }

    private static bool Compare(string group, BenchmarkReport candidate, BenchmarkReport baseline)
    {
        var passed = candidate.ResultStatistics!.Mean < baseline.ResultStatistics!.Mean && Allocated(candidate) <= Allocated(baseline);
        Report(passed ? "PASS" : "FAIL", group, candidate, baseline);
        return passed;
    }

    private static void Report(string verdict, string group, BenchmarkReport candidate, BenchmarkReport baseline)
    {
        var mean = candidate.ResultStatistics!.Mean;
        var baselineMean = baseline.ResultStatistics!.Mean;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{verdict} {group} {candidate.BenchmarkCase.Descriptor.WorkloadMethod.Name}: " +
            $"time {mean:N0} ns vs {baselineMean:N0} ns ({baselineMean / mean:N2}x), " +
            $"allocated {Allocated(candidate):N0} B vs {Allocated(baseline):N0} B"));
    }

    private static long Allocated(BenchmarkReport report) =>
        report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0;
}