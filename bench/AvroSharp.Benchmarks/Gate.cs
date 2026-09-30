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
/// <remarks>
/// A benchmark the gate can't check fails it, so a class can't drop out of the gate unnoticed (#157): an
/// <c>AvroSharp_*</c> benchmark whose group has no baseline, an <c>ApacheAvro_*</c> one that isn't its group's baseline,
/// and one named neither way. Benchmarks with no Apache.Avro equivalent say so with <see cref="Ungated"/>.
/// </remarks>
internal static class Gate
{
    /// <summary>Marks a benchmark that is reported next to the baseline but not gated (for example Chr.Avro).</summary>
    public const string ReferenceOnly = "ReferenceOnly";

    /// <summary>Marks a benchmark of AvroSharp alone, which Apache.Avro has no equivalent for.</summary>
    public const string Ungated = "Ungated";

    public static int Evaluate(IEnumerable<Summary> summaries)
    {
        var tally = new Tally();
        foreach (var group in summaries.SelectMany(Groups))
        {
            var baseline = group.SingleOrDefault(r => r.BenchmarkCase.Descriptor.Baseline);
            foreach (var report in group)
            {
                Check(group.Key, report, baseline, tally);
            }
        }

        if (tally.Compared == 0 && tally.Ungateable == 0)
        {
            Console.WriteLine(tally.Ungated > 0
                ? $"Gate: nothing to compare; {tally.Ungated} benchmarks have no Apache.Avro equivalent ({BuildInfo.Version})."
                : "GATE FAILED: no AvroSharp benchmark was compared against a baseline.");
            return tally.Ungated > 0 ? 0 : 1;
        }

        var notGated = tally.Ungateable == 0 ? string.Empty : $"; {tally.Ungateable} benchmarks can't be gated";
        Console.WriteLine($"Gate: {tally.Compared - tally.Failed}/{tally.Compared} comparisons passed{notGated} ({BuildInfo.Version}).");
        return tally.Failed > 0 || tally.Ungateable > 0 ? 1 : 0;
    }

    private static IEnumerable<IGrouping<string, BenchmarkReport>> Groups(Summary summary) =>
        summary.Reports
            .Where(r => r.ResultStatistics is not null)
            .GroupBy(r => string.Join(
                "|",
                r.BenchmarkCase.Descriptor.Type.Name,
                string.Join(",", r.BenchmarkCase.Descriptor.Categories.Where(c => !string.Equals(c, ReferenceOnly, StringComparison.Ordinal))),
                r.BenchmarkCase.Parameters.DisplayInfo,
                r.BenchmarkCase.Job.Environment.Runtime?.Name ?? "default"),
                StringComparer.Ordinal);

    private static void Check(string group, BenchmarkReport report, BenchmarkReport? baseline, Tally tally)
    {
        var descriptor = report.BenchmarkCase.Descriptor;
        var name = descriptor.WorkloadMethod.Name;
        if (descriptor.Categories.Contains(Ungated, StringComparer.Ordinal))
        {
            tally.Ungated++;
        }
        else if (descriptor.Baseline && name.StartsWith("ApacheAvro_", StringComparison.Ordinal))
        {
            // The baseline itself.
        }
        else if (name.StartsWith("AvroSharp_", StringComparison.Ordinal) && baseline is not null)
        {
            tally.Compared++;
            tally.Failed += Compare(group, report, baseline) ? 0 : 1;
        }
        else if (name.StartsWith("AvroSharp_", StringComparison.Ordinal))
        {
            tally.Ungateable++;
            Console.WriteLine($"FAIL {group} {name}: no Apache.Avro baseline in its group; add one, or mark it {Ungated}.");
        }
        else if (baseline is not null && (name.StartsWith("AvroSharpScalar_", StringComparison.Ordinal) || descriptor.Categories.Contains(ReferenceOnly, StringComparer.Ordinal)))
        {
            // Reference implementations such as AvroSharpScalar_* and Chr.Avro, reported but not gated.
            Report("INFO", group, report, baseline);
        }
        else
        {
            tally.Ungateable++;
            Console.WriteLine($"FAIL {group} {name}: not gated; name it AvroSharp_* or ApacheAvro_* (the group's baseline), or mark it {ReferenceOnly} or {Ungated}.");
        }
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

    private sealed class Tally
    {
        public int Compared { get; set; }

        public int Failed { get; set; }

        public int Ungated { get; set; }

        public int Ungateable { get; set; }
    }
}
