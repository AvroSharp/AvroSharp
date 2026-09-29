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
    /// <returns>The exit code: <see cref="Success"/>, <see cref="Failure"/> or <see cref="UsageError"/>.</returns>
    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        var root = CreateRootCommand(input, output, error);
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

    private static Command CreateRootCommand(TextReader input, TextWriter output, TextWriter error)
    {
        // A plain command, not RootCommand, whose name comes from the executable: run with dotnet, that would be AvroSharp.Tool.
        var root = new Command(
            "avrosharp",
            """
            AvroSharp's command-line tool for Apache Avro: generate C# from schema files, and print the canonical form
            and fingerprint of a schema.

            Examples:
              avrosharp gen schemas/ --output Generated/ --namespace Acme.Events
              avrosharp schema canonical user.avsc
              avrosharp schema fingerprint user.avsc --algorithm sha256
            """);
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());
        root.Subcommands.Add(GenCommand.Create(output, error));
        root.Subcommands.Add(SchemaCommands.Create(input, output, error));
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
