using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tool;

/// <summary>What the <c>file</c> commands do (<see cref="FileCommands"/> defines them). Each returns an exit code.</summary>
internal sealed class FileActions(ToolConsole console)
{
    /// <summary>A file name that means standard input or output.</summary>
    public const string Standard = "-";

    // Files may use every codec of the specification.
    private static readonly AvroFileReaderOptions s_readerOptions = new() { Codecs = AvroCodecs.All, LeaveOpen = true };

    public int ToJson(string file, bool pretty, string? readerSchemaPath) => Run(() =>
    {
        var readerSchema = readerSchemaPath is null ? null : ReadSchema(readerSchemaPath);
        using var input = OpenInput(file);
        using var reader = AvroFileReader.OpenGeneric(input, readerSchema, s_readerOptions);
        var json = GenericDatumJsonWriter.Create(readerSchema ?? reader.WriterSchema);
        foreach (var value in reader.ReadAll())
        {
            console.Output.WriteLine(json.WriteToString(value, pretty));
        }
    });

    public int FromJson(string jsonPath, string schemaPath, string outputPath, AvroCodec codec) => Run(() =>
    {
        var schema = ReadSchema(schemaPath);
        var json = ReadAllBytes(jsonPath);

        var jsonReader = GenericDatumJsonReader.Create(schema);
        using var output = OpenOutput(outputPath);
        using var writer = AvroFileWriter.CreateGeneric(output, schema, new AvroFileWriterOptions { Codec = codec, LeaveOpen = true });

        // Values one after another, separated by whitespace: each is read by a reader of the rest of the text.
        ReadOnlySpan<byte> data = json.AsSpan(json.AsSpan().StartsWith("﻿"u8) ? 3 : 0);
        for (var number = 1; SkipWhitespace(ref data); number++)
        {
            var reader = new Utf8JsonReader(data);
            try
            {
                writer.Write(jsonReader.Read(ref reader));
            }
            catch (Exception ex) when (ex is AvroException or JsonException)
            {
                throw new AvroException($"Value {number}: {ex.Message}", ex);
            }

            data = data[(int)reader.BytesConsumed..];
        }
    });

    public int GetSchema(string file) => Run(() =>
    {
        using var input = OpenInput(file);
        using var reader = AvroFileReader.OpenGeneric(input, options: s_readerOptions);
        console.Output.WriteLine(reader.WriterSchema.ToJson());
    });

    public int GetMeta(string file, string? key) => Run(() =>
    {
        using var input = OpenInput(file);
        using var reader = AvroFileReader.OpenGeneric(input, options: s_readerOptions);
        if (key is null)
        {
            foreach (var entry in reader.Metadata)
            {
                console.Output.WriteLine($"{entry.Key}\t{Encoding.UTF8.GetString(entry.Value.Span)}");
            }
        }
        else if (reader.TryGetMetadataString(key, out var value))
        {
            console.Output.WriteLine(value);
        }
        else
        {
            throw new AvroException($"The header has no '{key}' entry.");
        }
    });

    public int Count(string[] files) => Run(() =>
    {
        foreach (var file in files)
        {
            using var input = OpenInput(file);
            using var reader = AvroFileReader.OpenGeneric(input, options: s_readerOptions);
            var count = 0L;
            while (reader.TryReadBlock(out var block))
            {
                count += block.Count;
            }

            var text = count.ToString(CultureInfo.InvariantCulture);
            console.Output.WriteLine(files.Length == 1 ? text : $"{file}: {text}");
        }
    });

    public int Concat(string[] files, string outputPath) => Run(() =>
    {
        using var inputs = new DisposableList<Stream>(files.Select(OpenInput));
        using var output = OpenOutput(outputPath);
        AvroFile.Concat(output, inputs.Items, s_readerOptions);
    });

    public int Cat(string[] files, long offset, long? limit, string outputPath, AvroCodec? codec)
    {
        if (offset == 0 && limit is null && codec is null)
        {
            return Concat(files, outputPath);
        }

        return Run(() =>
        {
            using var inputs = new DisposableList<Stream>(files.Select(OpenInput));
            using var readers = new DisposableList<AvroFileReader<AvroValue>>(inputs.Items.Select(input => AvroFileReader.OpenGeneric(input, options: s_readerOptions)));
            var first = readers.Items[0];
            using var output = OpenOutput(outputPath);
            using var writer = AvroFileWriter.CreateGeneric(output, first.WriterSchema, new AvroFileWriterOptions
            {
                Codec = codec ?? first.Codec,
                Metadata = UserMetadata(first.Metadata),
                LeaveOpen = true,
            });
            var skip = offset;
            var left = limit ?? long.MaxValue;
            for (var i = 0; i < files.Length && left > 0; i++)
            {
                RequireSameSchema(readers.Items[i].WriterSchema, writer.Schema, files[i]);
                foreach (var value in readers.Items[i].ReadAll().TakeWhile(_ => left > 0))
                {
                    if (skip > 0)
                    {
                        skip--;
                        continue;
                    }

                    writer.Write(value);
                    left--;
                }
            }
        });
    }

    public int Recodec(string file, string outputPath, AvroCodec codec, bool recompress) => Run(() =>
    {
        using var input = OpenInput(file);
        using var reader = AvroFileReader.OpenGeneric(input, options: s_readerOptions);
        using var output = OpenOutput(outputPath);
        using var writer = AvroFileWriter.CreateGeneric(output, reader.WriterSchema, new AvroFileWriterOptions
        {
            Codec = codec,
            Metadata = UserMetadata(reader.Metadata),
            LeaveOpen = true,
        });
        while (reader.TryReadBlock(out var block))
        {
            // A level asks for compression again even with the same codec.
            writer.WriteBlock(block, recompress);
        }
    });

    public int Random(string schemaPath, int count, int? seed, string outputPath, AvroCodec codec) => Run(() =>
    {
        var schema = ReadSchema(schemaPath);
        var values = AvroRandomData.Generate(schema, seed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue));
        using var output = OpenOutput(outputPath);
        using var writer = AvroFileWriter.CreateGeneric(output, schema, new AvroFileWriterOptions { Codec = codec, LeaveOpen = true });
        foreach (var value in values.Take(count))
        {
            writer.Write(value);
        }
    });

    public int Repair(string file, string outputPath) => Run(() =>
    {
        var data = ReadAllBytes(file);
        using var output = OpenOutput(outputPath);
        var report = FileRepair.Repair(data, output, s_readerOptions);
        console.Error.WriteLine(report);
    });

    /// <summary>Disposes the items it holds, all of them, in order.</summary>
    private sealed class DisposableList<T> : IDisposable
        where T : IDisposable
    {
        public DisposableList(IEnumerable<T> items)
        {
            try
            {
                foreach (var item in items)
                {
                    Items.Add(item);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public List<T> Items { get; } = [];

        public void Dispose() => Items.ForEach(item => item.Dispose());
    }

    /// <summary>The entries an application added: the avro.* ones belong to the writer.</summary>
    internal static Dictionary<string, ReadOnlyMemory<byte>>? UserMetadata(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> metadata)
    {
        var user = metadata
            .Where(e => !e.Key.StartsWith("avro.", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return user.Count == 0 ? null : user;
    }

    private static void RequireSameSchema(AvroSchema schema, AvroSchema expected, string file)
    {
        if (!string.Equals(schema.CanonicalForm, expected.CanonicalForm, StringComparison.Ordinal))
        {
            throw new AvroException($"'{file}' has the schema {schema.CanonicalForm}, not the first file's {expected.CanonicalForm}.");
        }
    }

    // Skips JSON whitespace; false when nothing is left.
    private static bool SkipWhitespace(ref ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        data = data[i..];
        return data.Length > 0;
    }

    private static AvroSchema ReadSchema(string path) => AvroSchema.Parse(File.ReadAllText(path));

    /// <summary>Runs a command, reporting the failures of its input and output as errors.</summary>
    private int Run(Action action)
    {
        try
        {
            action();
            return Cli.Success;
        }
        catch (AvroSchemaException ex)
        {
            Diagnostics.Error(console.Error, $"Invalid schema: {ex.Message}");
        }
        catch (Exception ex) when (ex is AvroException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Diagnostics.Error(console.Error, ex.Message);
        }

        return Cli.Failure;
    }

    // Standard input is read whole, so readers that seek work on it.
    private Stream OpenInput(string path) =>
        string.Equals(path, Standard, StringComparison.Ordinal) ? new MemoryStream(ReadAllBytes(path), writable: false) : File.OpenRead(path);

    private byte[] ReadAllBytes(string path)
    {
        if (!string.Equals(path, Standard, StringComparison.Ordinal))
        {
            return File.ReadAllBytes(path);
        }

        var bytes = new System.Buffers.ArrayBufferWriter<byte>();
        using var input = console.OpenStandardInput();
        for (int read; (read = input.Read(bytes.GetSpan(64 * 1024))) > 0;)
        {
            bytes.Advance(read);
        }

        return bytes.WrittenSpan.ToArray();
    }

    private Stream OpenOutput(string path)
    {
        if (string.Equals(path, Standard, StringComparison.Ordinal))
        {
            return console.OpenStandardOutput();
        }

        return File.Create(path);
    }
}
