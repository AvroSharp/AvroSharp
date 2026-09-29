using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.CommandLine;
using System.Text;
using AvroSharp.CodeGen;

namespace AvroSharp.Tool;

/// <summary>
/// <c>avrosharp gen</c>: C# types and serializers from <c>.avsc</c> files, as the source generator makes them, with the
/// same options (<see cref="CodeGenOptions"/>).
/// </summary>
internal static class GenCommand
{
    private const string Suffix = ".g.cs";

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static Command Create(TextWriter output, TextWriter error)
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "Schema files, or folders searched (with their subfolders) for *.avsc files. Files may refer to named types that other files define, in any order.",
            Arity = ArgumentArity.OneOrMore,
        };
        inputs.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: false));
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "The folder to write the generated .g.cs files to, in folders for their namespaces (com/example/Order.g.cs). It is created if it does not exist; other files in it are left alone.",
            Required = true,
        };
        var flat = new Option<bool>("--flat")
        {
            Description = "Write every file into the output folder itself, named by the type's full name (com.example.Order.g.cs), instead of into folders for its namespace.",
        };
        var options = new GenOptions();

        var command = new Command("gen", "Generate C# types and serializers from Avro schema files (.avsc).");
        command.Arguments.Add(inputs);
        command.Options.Add(outputOption);
        command.Options.Add(flat);
        options.AddTo(command);
        command.SetAction(result => Run(result.GetValue(inputs)!, result.GetValue(outputOption)!, result.GetValue(flat), options.ToCodeGenOptions(result), output, error));
        return command;
    }

    private static int Run(string[] inputs, string outputFolder, bool flat, CodeGenOptions options, TextWriter output, TextWriter error)
    {
        if (!InputFiles.TryRead(inputs, error, out var files))
        {
            return Cli.Failure;
        }

        var set = SchemaFileSet.Parse(files);
        if (set.Errors.Count > 0)
        {
            foreach (var path in set.Errors.Keys.OrderBy(path => path, StringComparer.Ordinal))
            {
                Diagnostics.SchemaError(error, path, set.Errors[path], set.GetErrorMessage(path));
            }

            error.WriteLine($"No files written: {set.Errors.Count} of {files.Count} schema file(s) are not valid.");
            return Cli.Failure;
        }

        IReadOnlyList<GeneratedSource> sources;
        try
        {
            sources = CSharpCodeGenerator.Generate(set.Schemas, options);
        }
        catch (Exception ex) when (ex is AvroException or ArgumentException or InvalidOperationException)
        {
            Diagnostics.Error(error, Diagnostics.GenerationFailed, ex.Message);
            error.WriteLine("No files written.");
            return Cli.Failure;
        }

        if (!TryWrite(sources, outputFolder, flat, error))
        {
            return Cli.Failure;
        }

        foreach (var source in sources)
        {
            var typeName = source.HintName[..^Suffix.Length];
            var path = set.DefinedIn.TryGetValue(typeName, out var file) ? file : null;
            foreach (var note in source.Notes)
            {
                Diagnostics.Info(output, path, Diagnostics.NameChanged, note);
            }
        }

        output.WriteLine($"Generated {sources.Count} file(s) from {files.Count} schema file(s) in {outputFolder}.");
        return Cli.Success;
    }

    /// <summary>
    /// A generated file's path under the output folder: in folders for its C# namespace (<c>com/example/Order.g.cs</c>),
    /// or with <paramref name="flat"/>, named by its C# full name in the folder itself (<c>com.example.Order.g.cs</c>). The
    /// namespace is the generated one, after <c>--namespace</c> and <c>--namespace-map</c>; keyword escapes (@) are dropped.
    /// </summary>
    internal static string RelativePath(GeneratedSource source, bool flat)
    {
        var fullName = source.HintName[..^Suffix.Length];
        var name = fullName[(fullName.LastIndexOf('.') + 1)..] + Suffix;
        var ns = source.Namespace?.Replace("@", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(ns))
        {
            return name;
        }

        return flat ? ns + "." + name : Path.Combine([.. ns.Split('.'), name]);
    }

    private static bool TryWrite(IReadOnlyList<GeneratedSource> sources, string outputFolder, bool flat, TextWriter error)
    {
        // Paths that differ only by case are one file on Windows and macOS, where the second silently replaced the
        // first (#131). They are rejected everywhere, since the output is often checked in and used on each.
        var byPath = new Dictionary<string, GeneratedSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var path = RelativePath(source, flat);
            if (byPath.TryGetValue(path, out var other))
            {
                Diagnostics.Error(
                    error,
                    Diagnostics.GenerationFailed,
                    $"The types '{other.HintName[..^Suffix.Length]}' and '{source.HintName[..^Suffix.Length]}' would both be written to {path}: their names differ only by case, which Windows and macOS file names don't tell apart. Rename one of the types, or map its namespace elsewhere.");
                error.WriteLine("No files written.");
                return false;
            }

            byPath.Add(path, source);
        }

        try
        {
            foreach (var source in sources)
            {
                var path = Path.Combine(outputFolder, RelativePath(source, flat));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source.Text, s_utf8);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Error(error, $"Cannot write to '{outputFolder}': {ex.Message}");
            return false;
        }
    }
}
