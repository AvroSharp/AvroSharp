using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Help;

namespace AvroSharp.Tool;

/// <summary>The <c>avrosharp</c> command line: its commands, and how their results become exit codes.</summary>
internal static class Cli
{
    /// <summary>The command ran and did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The command ran and failed: an invalid schema, a missing file, a file that could not be written.</summary>
    public const int Failure = 1;

    /// <summary>The command line was not valid: an unknown command or option, a missing or invalid argument.</summary>
    public const int UsageError = 2;

    /// <summary>Runs the tool.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="input">Where a schema given as - (standard input) is read from.</param>
    /// <param name="output">Where results and help go.</param>
    /// <param name="error">Where errors go.</param>
    /// <returns>
    /// The exit code: <see cref="Success"/>, <see cref="Failure"/> or <see cref="UsageError"/>, and for <c>schema compat</c>
    /// <see cref="CompatCommand.Partial"/> or <see cref="CompatCommand.Incompatible"/>.
    /// </returns>
    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error) =>
        Run(args, new ToolConsole(input, output, error, Console.OpenStandardInput, Console.OpenStandardOutput));

    /// <summary>Runs the tool, with container files given as <c>-</c> read from and written to the console's streams.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="console">Where the tool reads and writes.</param>
    /// <returns>The exit code, as <see cref="Run(string[], TextReader, TextWriter, TextWriter)"/> returns it.</returns>
    public static int Run(string[] args, ToolConsole console)
    {
        var (output, error) = (console.Output, console.Error);
        var root = CreateRootCommand(console);
        var result = root.Parse(args);
        if (result.Errors.Count > 0)
        {
            foreach (var parseError in result.Errors)
            {
                error.WriteLine($"error: {parseError.Message}");
            }

            var command = result.CommandResult.Command;
            var name = command == root ? "avrosharp" : $"avrosharp {GetCommandPath(command)}";
            error.WriteLine($"Run '{name} --help' for usage.");
            return UsageError;
        }

        return result.Invoke(new InvocationConfiguration { Output = output, Error = error });
    }

    private static Command CreateRootCommand(ToolConsole console)
    {
        var (input, output, error) = (console.Input, console.Output, console.Error);
        // A plain command, not RootCommand, whose name comes from the executable: run with dotnet, that would be AvroSharp.Tool.
        var root = new Command(
            "avrosharp",
            """
            AvroSharp's command-line tool for Apache Avro: generate C# from schema files, print the canonical form
            and fingerprint of a schema, check schema compatibility, and work with container files.

            Examples:
              avrosharp gen schemas/ --output Generated/ --namespace Acme.Events
              avrosharp schema canonical user.avsc
              avrosharp schema fingerprint user.avsc --algorithm sha256
              avrosharp schema compat v1.avsc v2.avsc
              avrosharp file tojson events.avro
              avrosharp file recodec events.avro --codec zstandard -o events.zstd.avro
            """);
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());
        root.Subcommands.Add(GenCommand.Create(output, error));
        root.Subcommands.Add(SchemaCommands.Create(input, output, error));
        root.Subcommands.Add(FileCommands.Create(console));
        return root;
    }

    private static string GetCommandPath(Command command)
    {
        var names = new List<string>();
        for (var current = command; current?.Parents.OfType<Command>().FirstOrDefault() is { } parent; current = parent)
        {
            names.Insert(0, current.Name);
        }

        return string.Join(' ', names);
    }
}
