using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

namespace AvroSharp.Tool;

/// <summary>
/// <c>avrosharp schema canonical|fingerprint</c>: the Parsing Canonical Form and fingerprints of one or more schemas;
/// <c>avrosharp schema compat</c> is in <see cref="CompatCommand"/>.
/// </summary>
internal static class SchemaCommands
{
    private const string StandardInput = "-";
    private const string StandardInputName = "<stdin>";
    private const string Crc64 = "crc64";
    private const string Md5 = "md5";
    private const string Sha256 = "sha256";
    private const string Hex = "hex";
    private const string Base64 = "base64";
    private const string Decimal = "decimal";

    public static Command Create(TextReader input, TextWriter output, TextWriter error)
    {
        var command = new Command("schema", "Print the Parsing Canonical Form or a fingerprint of schemas, or check whether one schema can read another's data.");
        command.Subcommands.Add(CreateCanonical(input, output, error));
        command.Subcommands.Add(CreateFingerprint(input, output, error));
        command.Subcommands.Add(CompatCommand.Create(output, error));
        return command;
    }

    private static Argument<string[]> InputsArgument()
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "Schema files, folders searched for *.avsc files, or - to read one schema from standard input. They may refer to each other's named types, in any order. With one schema the result is printed alone; with several, as 'path: result' lines.",
            Arity = ArgumentArity.OneOrMore,
        };
        inputs.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: true));
        inputs.Validators.Add(result =>
        {
            var values = result.GetValueOrDefault<string[]>();
            if (values.Length > 1 && values.Contains(StandardInput, StringComparer.Ordinal))
            {
                result.AddError("'-' (standard input) cannot be combined with other inputs.");
            }
        });
        return inputs;
    }

    private static Option<string[]> ReferenceOption() => new("--reference", "-r")
    {
        Description = "Schema files, or folders searched for *.avsc files, that define named types the inputs use, without printing a result for them. Repeat the option for several.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = true,
    };

    private static Command CreateCanonical(TextReader input, TextWriter output, TextWriter error)
    {
        var inputs = InputsArgument();
        var references = ReferenceOption();
        var command = new Command(
            "canonical",
            "Print the Parsing Canonical Form of schemas, as the Avro specification defines it: the form that fingerprints and schema comparison use.");
        command.Arguments.Add(inputs);
        command.Options.Add(references);
        command.SetAction(result => Run(result.GetValue(inputs)!, result.GetValue(references), input, output, error, schema => schema.CanonicalForm));
        return command;
    }

    private static Command CreateFingerprint(TextReader input, TextWriter output, TextWriter error)
    {
        var inputs = InputsArgument();
        var references = ReferenceOption();
        var algorithm = new Option<string>("--algorithm", "-a")
        {
            Description = "crc64: the 64-bit CRC-64-AVRO (Rabin) fingerprint, which single-object encoding and schema stores use. md5 or sha256: those digests of the canonical form.",
            DefaultValueFactory = _ => Crc64,
        };
        algorithm.AcceptOnlyFromAmong(Crc64, Md5, Sha256);
        var format = new Option<string>("--format", "-f")
        {
            Description = "hex: the fingerprint's bytes as Avro writes them (CRC-64 little-endian, as in single-object encoding). base64: the same bytes. decimal: the CRC-64 as a signed 64-bit number, as Java's SchemaNormalization.parsingFingerprint64 returns it (crc64 only).",
            DefaultValueFactory = _ => Hex,
        };
        format.AcceptOnlyFromAmong(Hex, Base64, Decimal);

        var command = new Command("fingerprint", "Print a fingerprint of the Parsing Canonical Form of schemas.");
        command.Arguments.Add(inputs);
        command.Options.Add(references);
        command.Options.Add(algorithm);
        command.Options.Add(format);
        command.Validators.Add(result =>
        {
            // In a validator, an option not on the command line reads as null, not as its default.
            if (Is(result.GetValue(format), Decimal) && !Is(result.GetValue(algorithm) ?? Crc64, Crc64))
            {
                result.AddError("--format decimal needs --algorithm crc64.");
            }
        });
        command.SetAction(result =>
        {
            var chosenAlgorithm = result.GetValue(algorithm)!;
            var chosenFormat = result.GetValue(format)!;
            return Run(result.GetValue(inputs)!, result.GetValue(references), input, output, error, schema => Fingerprint(schema, chosenAlgorithm, chosenFormat));
        });
        return command;
    }

    /// <summary>Parses the inputs with the references, and prints a result for each input.</summary>
    private static int Run(string[] inputs, string[]? references, TextReader input, TextWriter output, TextWriter error, Func<AvroSchema, string> describe)
    {
        if (!TryReadInputs(inputs, input, error, out var files) || !TryReadReferences(references, error, out var referenceFiles))
        {
            return Cli.Failure;
        }

        // A reference folder may hold the inputs themselves; each file is parsed once.
        var inputPaths = files.Select(file => FullPath(file.Path)).ToHashSet(PathComparer);
        var set = SchemaFileSet.Parse(referenceFiles.Where(file => !inputPaths.Contains(FullPath(file.Path))).Concat(files));
        if (set.Errors.Count > 0)
        {
            foreach (var path in set.Errors.Keys.Order(StringComparer.Ordinal))
            {
                Diagnostics.SchemaError(error, path, set.Errors[path], set.GetErrorMessage(path));
            }

            return Cli.Failure;
        }

        foreach (var (path, _) in files)
        {
            var text = describe(set.SchemasByPath[path]);
            output.WriteLine(files.Count == 1 ? text : $"{path}: {text}");
        }

        return Cli.Success;
    }

    private static bool TryReadInputs(string[] inputs, TextReader input, TextWriter error, out List<(string Path, string Json)> files)
    {
        if (!inputs.Contains(StandardInput, StringComparer.Ordinal))
        {
            return InputFiles.TryRead(inputs, error, out files);
        }

        files = [];
        try
        {
            files.Add((StandardInputName, input.ReadToEnd()));
            return true;
        }
        catch (IOException ex)
        {
            Diagnostics.Error(error, $"Cannot read standard input: {ex.Message}");
            return false;
        }
    }

    private static bool TryReadReferences(string[]? references, TextWriter error, out List<(string Path, string Json)> files)
    {
        if (references is not { Length: > 0 })
        {
            files = [];
            return true;
        }

        return InputFiles.TryRead(references, error, out files);
    }

    private static string Fingerprint(AvroSchema schema, string algorithm, string format)
    {
        if (Is(format, Decimal))
        {
            return schema.Fingerprint64.ToString(CultureInfo.InvariantCulture);
        }

        byte[] bytes;
        if (Is(algorithm, Md5))
        {
            bytes = SchemaFingerprint.Md5(schema);
        }
        else if (Is(algorithm, Sha256))
        {
            bytes = SchemaFingerprint.Sha256(schema);
        }
        else
        {
            bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, schema.Fingerprint64);
        }

        return Is(format, Base64) ? Convert.ToBase64String(bytes) : Convert.ToHexStringLower(bytes);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FullPath(string path) => Is(path, StandardInputName) ? path : Path.GetFullPath(path);

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.Ordinal);
}
