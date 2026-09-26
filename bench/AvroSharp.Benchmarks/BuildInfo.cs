using System.Reflection;
using AvroSharp.IO;

namespace AvroSharp.Benchmarks;

/// <summary>Identifies the AvroSharp build being measured, so every result records which code it came from.</summary>
internal static class BuildInfo
{
    /// <summary>
    /// Gets the AvroSharp informational version; SourceLink appends the commit, for example "0.0.0-alpha.0.7+1a2b3c4".
    /// </summary>
    public static string Version { get; } =
        "AvroSharp " + (typeof(AvroWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
}