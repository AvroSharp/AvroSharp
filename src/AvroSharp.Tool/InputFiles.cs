using System;
using System.Collections.Generic;
using System.IO;
using System.CommandLine.Parsing;
using System.Linq;

namespace AvroSharp.Tool;

/// <summary>Schema files named on the command line: files, and folders searched for *.avsc files.</summary>
internal static class InputFiles
{
    /// <summary>
    /// Rejects an input that looks like an option, such as a misspelled one: the parser would take it as a file name.
    /// Standard input is <c>-</c>, where <paramref name="allowStandardInput"/> says so.
    /// </summary>
    public static void RejectOptionLikeInputs(ArgumentResult result, bool allowStandardInput)
    {
        var optionLike = result.Tokens.Where(token => token.Value.StartsWith('-') && !(allowStandardInput && string.Equals(token.Value, "-", StringComparison.Ordinal)));
        foreach (var token in optionLike)
        {
            result.AddError($"Unrecognized command or argument '{token.Value}'.");
        }
    }

    /// <summary>Reads the schema files: each file argument, and every *.avsc file under each folder argument.</summary>
    public static bool TryRead(string[] inputs, TextWriter error, out List<(string Path, string Json)> files)
    {
        files = [];
        var paths = new List<string>();
        var ok = true;
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var found = Directory.EnumerateFiles(input, "*.avsc", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
                if (found.Count == 0)
                {
                    Diagnostics.Error(error, $"No .avsc files in '{input}'.");
                    ok = false;
                }

                paths.AddRange(found);
            }
            else if (File.Exists(input))
            {
                paths.Add(input);
            }
            else
            {
                Diagnostics.Error(error, $"'{input}' is not a file or folder.");
                ok = false;
            }
        }

        // A file named twice, or found both on its own and under a folder, is read once.
        foreach (var path in paths.DistinctBy(Path.GetFullPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            try
            {
                files.Add((path, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Error(error, $"Cannot read '{path}': {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }
}
