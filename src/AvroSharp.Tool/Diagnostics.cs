using System.IO;

namespace AvroSharp.Tool;

/// <summary>
/// Diagnostics in the compiler's format, <c>path(line,column): error ID: message</c>, so editors and CI logs link them
/// to the file. The IDs are the source generator's (AnalyzerReleases.Shipped.md).
/// </summary>
internal static class Diagnostics
{
    public const string InvalidSchema = "AVROGEN001";
    public const string GenerationFailed = "AVROGEN003";
    public const string NameChanged = "AVROGEN005";

    public static void SchemaError(TextWriter error, string path, AvroSchemaException exception, string? message = null) =>
        error.WriteLine($"{path}({exception.LineNumber ?? 1},{exception.BytePositionInLine ?? 1}): error {InvalidSchema}: {message ?? exception.Message}");

    public static void Error(TextWriter error, string message) => error.WriteLine($"error: {message}");

    public static void Error(TextWriter error, string id, string message) => error.WriteLine($"error {id}: {message}");

    public static void Info(TextWriter output, string? path, string id, string message) =>
        output.WriteLine(path is null ? $"info {id}: {message}" : $"{path}: info {id}: {message}");
}
