using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using AvroSharp.IO;

namespace AvroSharp.Benchmarks;

/// <summary>Identifies the AvroSharp build being measured, so every result records which code it came from.</summary>
internal static class BuildInfo
{
    /// <summary>Gets the target framework of the AvroSharp assembly that was loaded, for example ".NETCoreApp,Version=v10.0".</summary>
    public static string TargetFramework { get; } =
        typeof(AvroWriter).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";

    /// <summary>
    /// Gets the AvroSharp informational version and target framework; SourceLink appends the commit, for example
    /// "0.0.0-alpha.0.7+1a2b3c4".
    /// </summary>
    public static string Version { get; } =
        "AvroSharp " + (typeof(AvroWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
        + " (" + TargetFramework + ")";

    /// <summary>
    /// Refuses to run against AvroSharp's netstandard build. BenchmarkDotNet builds every project into one output
    /// folder, and a netstandard2.0 copy of AvroSharp (the one the source generator loads) once replaced the net10.0
    /// one there, silently measuring code without its net8+ paths. Runs in every benchmark process, host and children.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureModernBuild()
    {
        if (TargetFramework.StartsWith(".NETStandard", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The benchmarks loaded AvroSharp's {TargetFramework} build instead of a .NET build; results would not measure the net8+ code paths.");
        }
    }
}
