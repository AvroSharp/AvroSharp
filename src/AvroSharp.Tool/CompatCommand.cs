using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

namespace AvroSharp.Tool;

/// <summary>
/// <c>avrosharp schema compat</c>: whether data written with one schema can be read with another, or whether a new
/// version passes a schema registry's compatibility level against earlier ones (<see cref="AvroSchemaCompatibility"/>).
/// </summary>
internal static class CompatCommand
{
    /// <summary>Some values written with the writer's schema cannot be read: an enum symbol or a union branch the reader lacks.</summary>
    public const int Partial = 3;

    /// <summary>The schemas are incompatible.</summary>
    public const int Incompatible = 4;

    // The JSON output's shape: a change that would break scripts parsing it gets a new number.
    private const int FormatVersion = 1;

    private static readonly string[] s_levels = ["backward", "backward-transitive", "forward", "forward-transitive", "full", "full-transitive"];

    public static Command Create(TextWriter output, TextWriter error)
    {
        var schemas = new Argument<string[]>("schemas")
        {
            Description = "Without --level: the writer's schema file, then the reader's. With --level: the earlier versions, oldest first, then the new version.",
            Arity = new ArgumentArity(2, 100_000),
        };
        schemas.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: false));
        var level = new Option<string>("--level", "-l")
        {
            Description = "Check the last schema against the earlier ones at a schema registry's level, as Confluent Schema Registry defines them: backward (the new schema reads the latest earlier version's data), forward (the latest earlier version reads the new schema's data) or full (both); a -transitive level checks every earlier version.",
        };
        level.AcceptOnlyFromAmong(s_levels);
        var references = new Option<string[]>("--reference", "-r")
        {
            Description = "Schema files, or folders searched for *.avsc files, that define named types the schemas use. Repeat the option for several.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var warningsAsErrors = new Option<bool>("--warnings-as-errors", "--strict") { Description = "Fail on warnings too: differences the specification allows that can change the values read, such as a decimal's scale. The verdict stays what it is." };
        var allowPartial = new Option<bool>("--allow-partial") { Description = "Pass when only some values cannot be read (an enum symbol or a union branch the reader lacks)." };

        var command = new Command(
            "compat",
            """
            Check whether data written with one schema can be read with another, with every reason it cannot. Exit codes:
            0 compatible, 1 a schema could not be read, 2 invalid command line, 3 partially compatible (some values cannot
            be read), 4 incompatible, or warnings with --warnings-as-errors.

            Examples:
              avrosharp schema compat v1.avsc v2.avsc
              avrosharp schema compat --level backward-transitive v1.avsc v2.avsc v3.avsc
            """);
        command.Arguments.Add(schemas);
        command.Options.Add(level);
        command.Options.Add(references);
        command.Options.Add(json);
        command.Options.Add(warningsAsErrors);
        command.Options.Add(allowPartial);
        command.Validators.Add(result =>
        {
            // Results, not values: reading a value that failed its own validation throws.
            if (result.GetResult(level) is null && result.GetResult(schemas)?.Tokens.Count > 2)
            {
                result.AddError("Give two schemas, the writer's and the reader's, or use --level to check a new version against several.");
            }
        });
        command.SetAction(result =>
        {
            var options = new AvroCompatibilityOptions { WarningsAsErrors = result.GetValue(warningsAsErrors), AllowPartial = result.GetValue(allowPartial) };
            return Run(result.GetValue(schemas)!, result.GetValue(references), result.GetValue(level), options, result.GetValue(json), output, error);
        });
        return command;
    }

    private static int Run(string[] paths, string[]? references, string? level, AvroCompatibilityOptions options, bool json, TextWriter output, TextWriter error)
    {
        if (!TryParse(paths, references, error, out var schemas))
        {
            return Cli.Failure;
        }

        if (level is null)
        {
            var result = AvroSchemaCompatibility.Check(schemas[0], schemas[1], options);
            output.WriteLine(json ? Json(writer => WriteResult(writer, result, paths)) : result.ToString());
            return ExitCode(result.Verdict, result.IsCompatible);
        }

        var report = AvroSchemaCompatibility.CheckVersions(schemas[^1], schemas[..^1], ParseLevel(level), options);
        output.WriteLine(json ? Json(writer => WriteReport(writer, report, level, paths)) : Text(report, paths));
        return ExitCode(report.Verdict, report.IsCompatible);
    }

    private static int ExitCode(AvroCompatibilityVerdict verdict, bool isCompatible) =>
        isCompatible ? Cli.Success : verdict == AvroCompatibilityVerdict.Partial ? Partial : Incompatible;

    // Each schema is a version of its own, parsed apart from the others (versions define the same names), with the references.
    private static bool TryParse(string[] paths, string[]? references, TextWriter error, out AvroSchema[] schemas)
    {
        schemas = new AvroSchema[paths.Length];
        var referenceFiles = new List<(string Path, string Json)>();
        if (references is { Length: > 0 } && !InputFiles.TryRead(references, error, out referenceFiles))
        {
            return false;
        }

        var ok = true;
        for (var i = 0; i < paths.Length; i++)
        {
            if (TryRead(paths[i], error) is not { } text)
            {
                ok = false;
                continue;
            }

            var full = Path.GetFullPath(paths[i]);
            var set = SchemaFileSet.Parse(referenceFiles.Where(r => !PathComparer.Equals(Path.GetFullPath(r.Path), full)).Append((paths[i], text)));
            foreach (var path in set.Errors.Keys.Order(StringComparer.Ordinal))
            {
                Diagnostics.SchemaError(error, path, set.Errors[path], set.GetErrorMessage(path));
                ok = false;
            }

            if (set.SchemasByPath.TryGetValue(paths[i], out var schema))
            {
                schemas[i] = schema;
            }
        }

        return ok;
    }

    private static string? TryRead(string path, TextWriter error)
    {
        if (!File.Exists(path))
        {
            Diagnostics.Error(error, Directory.Exists(path) ? $"'{path}' is a folder; give each schema version as a file." : $"'{path}' is not a file.");
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Error(error, $"Cannot read '{path}': {ex.Message}");
            return null;
        }
    }

    private static AvroCompatibilityLevel ParseLevel(string level) => level switch
    {
        "backward" => AvroCompatibilityLevel.Backward,
        "backward-transitive" => AvroCompatibilityLevel.BackwardTransitive,
        "forward" => AvroCompatibilityLevel.Forward,
        "forward-transitive" => AvroCompatibilityLevel.ForwardTransitive,
        "full" => AvroCompatibilityLevel.Full,
        _ => AvroCompatibilityLevel.FullTransitive,
    };

    // As the report's own text, with each version named by its file rather than its index.
    private static string Text(AvroCompatibilityReport report, string[] paths)
    {
        var text = new StringBuilder(Describe(report.Verdict));
        foreach (var check in report.Checks)
        {
            text.AppendLine().Append(check.Direction == AvroCompatibilityDirection.Backward
                ? $"Reading {paths[check.Index]}'s data with {paths[^1]}: "
                : $"Reading {paths[^1]}'s data with {paths[check.Index]}: ").Append(check.Result);
        }

        return text.ToString();
    }

    private static string Describe(AvroCompatibilityVerdict verdict) => verdict switch
    {
        AvroCompatibilityVerdict.Compatible => "Compatible.",
        AvroCompatibilityVerdict.Partial => "Partially compatible: some values cannot be read.",
        _ => "Incompatible.",
    };

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteReport(Utf8JsonWriter writer, AvroCompatibilityReport report, string level, string[] paths)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("level", level);
        writer.WriteString("schema", paths[^1]);
        writer.WriteString("verdict", Verdict(report.Verdict));
        writer.WriteBoolean("compatible", report.IsCompatible);
        writer.WriteStartArray("checks");
        foreach (var check in report.Checks)
        {
            writer.WriteStartObject();
            writer.WriteString("previous", paths[check.Index]);
            writer.WriteNumber("index", check.Index);
            writer.WriteString("direction", check.Direction == AvroCompatibilityDirection.Backward ? "backward" : "forward");
            WriteResultProperties(writer, check.Result);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter writer, AvroCompatibilityResult result, string[] paths)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("writer", paths[0]);
        writer.WriteString("reader", paths[1]);
        WriteResultProperties(writer, result);
        writer.WriteEndObject();
    }

    private static void WriteResultProperties(Utf8JsonWriter writer, AvroCompatibilityResult result)
    {
        writer.WriteString("verdict", Verdict(result.Verdict));
        writer.WriteBoolean("compatible", result.IsCompatible);
        WriteIssues(writer, "incompatibilities", result.Incompatibilities);
        WriteIssues(writer, "warnings", result.Warnings);
    }

    private static void WriteIssues(Utf8JsonWriter writer, string name, IReadOnlyList<AvroCompatibilityIssue> issues)
    {
        writer.WriteStartArray(name);
        foreach (var issue in issues)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", JsonNamingPolicy.CamelCase.ConvertName(issue.Kind.ToString()));
            writer.WriteString("path", issue.Path);
            writer.WriteString("message", issue.Message);
            if (issue.OtherPaths.Count > 0)
            {
                writer.WriteStartArray("otherPaths");
                foreach (var path in issue.OtherPaths)
                {
                    writer.WriteStringValue(path);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Verdict(AvroCompatibilityVerdict verdict) => verdict switch
    {
        AvroCompatibilityVerdict.Compatible => "compatible",
        AvroCompatibilityVerdict.Partial => "partial",
        _ => "incompatible",
    };
}
