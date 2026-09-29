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
            Description = "The folder to write the generated .g.cs files to. It is created if it does not exist; other files in it are left alone.",
            Required = true,
        };
        var options = new GenOptions();

        var command = new Command("gen", "Generate C# types and serializers from Avro schema files (.avsc).");
        command.Arguments.Add(inputs);
        command.Options.Add(outputOption);
        options.AddTo(command);
        command.SetAction(result => Run(result.GetValue(inputs)!, result.GetValue(outputOption)!, options.ToCodeGenOptions(result), output, error));
        return command;
    }

    private static int Run(string[] inputs, string outputFolder, CodeGenOptions options, TextWriter output, TextWriter error)
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

        try
        {
            Directory.CreateDirectory(outputFolder);
            foreach (var source in sources)
            {
                File.WriteAllText(Path.Combine(outputFolder, source.HintName), source.Text, s_utf8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Error(error, $"Cannot write to '{outputFolder}': {ex.Message}");
            return Cli.Failure;
        }

        foreach (var source in sources)
        {
            var typeName = source.HintName[..^".g.cs".Length];
            var path = set.DefinedIn.TryGetValue(typeName, out var file) ? file : null;
            foreach (var note in source.Notes)
            {
                Diagnostics.Info(output, path, Diagnostics.PropertyRenamed, note);
            }
        }

        output.WriteLine($"Generated {sources.Count} file(s) from {files.Count} schema file(s) in {outputFolder}.");
        return Cli.Success;
    }
}
