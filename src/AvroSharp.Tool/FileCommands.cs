using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;

namespace AvroSharp.Tool;

/// <summary>
/// <c>avrosharp file ...</c>: Apache Avro Java's avro-tools commands for object container files. The work is in
/// <see cref="FileActions"/>.
/// </summary>
internal static class FileCommands
{
    public static Command Create(ToolConsole console)
    {
        var actions = new FileActions(console);
        var command = new Command("file", "Read, write, convert and repair Avro object container files (.avro), as Apache Avro Java's avro-tools does.");
        command.Subcommands.Add(ToJson(actions));
        command.Subcommands.Add(FromJson(actions));
        command.Subcommands.Add(GetSchema(actions));
        command.Subcommands.Add(GetMeta(actions));
        command.Subcommands.Add(Count(actions));
        command.Subcommands.Add(Cat(actions));
        command.Subcommands.Add(Concat(actions));
        command.Subcommands.Add(Recodec(actions));
        command.Subcommands.Add(Random(actions));
        command.Subcommands.Add(Repair(actions));
        return command;
    }

    private static Command ToJson(FileActions actions)
    {
        var file = FileArgument();
        var pretty = new Option<bool>("--pretty", "-p") { Description = "Indent each value, instead of one value per line." };
        var readerSchema = new Option<string?>("--reader-schema") { Description = "A schema file to read the values with, resolving them from the file's schema, as schema evolution does." };
        var command = new Command("tojson", "Print a file's values in the Avro JSON encoding, one per line, as avro-tools tojson does.");
        command.Arguments.Add(file);
        command.Options.Add(pretty);
        command.Options.Add(readerSchema);
        command.SetAction(result => actions.ToJson(result.GetValue(file)!, result.GetValue(pretty), result.GetValue(readerSchema)));
        return command;
    }

    private static Command FromJson(FileActions actions)
    {
        var json = new Argument<string>("json") { Description = "A file of JSON values in the Avro JSON encoding, one after another (as tojson prints them), or - for standard input." };
        json.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: true));
        var schema = SchemaOption();
        var output = OutputOption();
        var codec = new CodecOptions("null");
        var command = new Command("fromjson", "Write JSON values in the Avro JSON encoding to a container file, as avro-tools fromjson does.");
        command.Arguments.Add(json);
        command.Options.Add(schema);
        command.Options.Add(output);
        codec.AddTo(command);
        command.SetAction(result => actions.FromJson(result.GetValue(json)!, result.GetValue(schema)!, result.GetValue(output)!, codec.Get(result)!));
        return command;
    }

    private static Command GetSchema(FileActions actions)
    {
        var file = FileArgument();
        var command = new Command("getschema", "Print a file's schema, as avro-tools getschema does.");
        command.Arguments.Add(file);
        command.SetAction(result => actions.GetSchema(result.GetValue(file)!));
        return command;
    }

    private static Command GetMeta(FileActions actions)
    {
        var file = FileArgument();
        var key = new Option<string?>("--key", "-k") { Description = "Print only this entry's value." };
        var command = new Command("getmeta", "Print a file's header metadata, one 'key<TAB>value' line per entry (values as UTF-8), as avro-tools getmeta does.");
        command.Arguments.Add(file);
        command.Options.Add(key);
        command.SetAction(result => actions.GetMeta(result.GetValue(file)!, result.GetValue(key)));
        return command;
    }

    private static Command Count(FileActions actions)
    {
        var files = FilesArgument("Container files, or - for standard input. With one file the count is printed alone; with several, as 'path: count' lines.");
        var command = new Command("count", "Print the number of objects in files, from their blocks' counts, without decoding the objects.");
        command.Arguments.Add(files);
        command.SetAction(result => actions.Count(result.GetValue(files)!));
        return command;
    }

    private static Command Cat(FileActions actions)
    {
        var files = FilesArgument("Container files of one schema, or - for standard input.");
        var offset = new Option<long>("--offset") { Description = "Objects to skip first." };
        var limit = new Option<long?>("--limit") { Description = "The most objects to write." };
        offset.Validators.Add(result => RejectNegative(result, result.GetValueOrDefault<long>()));
        limit.Validators.Add(result => RejectNegative(result, result.GetValueOrDefault<long?>() ?? 0));
        var output = OutputOption();
        var codec = new CodecOptions(defaultCodec: null);
        var command = new Command("cat", "Write files' objects to one file, as avro-tools cat does. Without --offset, --limit and --codec, blocks are copied without decoding the objects.");
        command.Arguments.Add(files);
        command.Options.Add(offset);
        command.Options.Add(limit);
        command.Options.Add(output);
        codec.AddTo(command);
        command.SetAction(result => actions.Cat(result.GetValue(files)!, result.GetValue(offset), result.GetValue(limit), result.GetValue(output)!, codec.Get(result)));
        return command;
    }

    private static Command Concat(FileActions actions)
    {
        var files = FilesArgument("Container files of one schema, or - for standard input.");
        var output = OutputOption();
        var command = new Command("concat", "Concatenate files of one schema block by block, without decoding their objects, as avro-tools concat does. The output has the first file's codec and metadata; blocks of other codecs are recompressed.");
        command.Arguments.Add(files);
        command.Options.Add(output);
        command.SetAction(result => actions.Concat(result.GetValue(files)!, result.GetValue(output)!));
        return command;
    }

    private static Command Recodec(FileActions actions)
    {
        var file = FileArgument();
        var output = OutputOption();
        var codec = new CodecOptions("null");
        var command = new Command("recodec", "Write a file's blocks with another codec, without decoding their objects, as avro-tools recodec does. The metadata is kept.");
        command.Arguments.Add(file);
        command.Options.Add(output);
        codec.AddTo(command);
        command.SetAction(result => actions.Recodec(result.GetValue(file)!, result.GetValue(output)!, codec.Get(result)!, recompress: result.GetValue(codec.Level) is not null));
        return command;
    }

    private static Command Random(FileActions actions)
    {
        var schema = SchemaOption();
        var count = new Option<int>("--count", "-n") { Description = "The number of objects.", Required = true };
        count.Validators.Add(result => RejectNegative(result, result.GetValueOrDefault<int>()));
        var seed = new Option<int?>("--seed") { Description = "The seed: the same seed and schema give the same objects. Without it, a random one." };
        var output = OutputOption();
        var codec = new CodecOptions("null");
        var command = new Command("random", "Write random objects of a schema to a container file, as avro-tools random does: valid for every logical type, with recursive schemas kept small.");
        command.Options.Add(schema);
        command.Options.Add(count);
        command.Options.Add(seed);
        command.Options.Add(output);
        codec.AddTo(command);
        command.SetAction(result => actions.Random(result.GetValue(schema)!, result.GetValue(count), result.GetValue(seed), result.GetValue(output)!, codec.Get(result)!));
        return command;
    }

    private static Command Repair(FileActions actions)
    {
        var file = FileArgument();
        var output = OutputOption();
        var command = new Command("repair", "Copy the readable blocks of a damaged file: a block that is cut short, fails to decompress or decode, or isn't followed by the sync marker is skipped, up to the next sync marker. Prints what was kept and skipped.");
        command.Arguments.Add(file);
        command.Options.Add(output);
        command.SetAction(result => actions.Repair(result.GetValue(file)!, result.GetValue(output)!));
        return command;
    }

    private static Argument<string> FileArgument()
    {
        var file = new Argument<string>("file") { Description = "A container file, or - for standard input." };
        file.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: true));
        return file;
    }

    private static Argument<string[]> FilesArgument(string description)
    {
        var files = new Argument<string[]>("files") { Description = description, Arity = ArgumentArity.OneOrMore };
        files.Validators.Add(result => InputFiles.RejectOptionLikeInputs(result, allowStandardInput: true));
        files.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string[]>().Count(f => string.Equals(f, FileActions.Standard, StringComparison.Ordinal)) > 1)
            {
                result.AddError("'-' (standard input) can be given once.");
            }
        });
        return files;
    }

    private static Option<string> SchemaOption() => new("--schema", "-s") { Description = "The schema file (.avsc).", Required = true };

    private static Option<string> OutputOption() => new("--output", "-o") { Description = "The container file to write, or - for standard output.", Required = true };

    private static void RejectNegative(OptionResult result, long value)
    {
        if (value < 0)
        {
            result.AddError($"{result.Option.Name} can't be negative.");
        }
    }
}
