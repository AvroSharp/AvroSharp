using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvroSharp.Generic;

namespace AvroSharp.Containers;

/// <summary>Operations on whole object container files.</summary>
/// <seealso cref="AvroFileReader"/>
/// <seealso cref="AvroFileWriter"/>
public static class AvroFile
{
    /// <summary>
    /// Concatenates container files of one schema into one file, block by block, without decoding their objects, as
    /// Apache Avro Java's <c>avro-tools concat</c> does. The output has the first input's codec and metadata; blocks of
    /// that codec are copied byte for byte, and blocks of any other codec are recompressed. The streams are left open.
    /// When an input fails, the output holds the header and the blocks copied before it: write to a temporary file and
    /// keep it only on success.
    /// </summary>
    /// <param name="output">The destination.</param>
    /// <param name="inputs">The files, at least one.</param>
    /// <param name="options">
    /// How to read the inputs, or <see langword="null"/> for the defaults: <see cref="AvroFileReaderOptions.Codecs"/> must
    /// have the codecs of the inputs beyond null and deflate.
    /// </param>
    /// <exception cref="ArgumentException">There are no inputs.</exception>
    /// <exception cref="AvroException">An input's schema is not the first input's (by canonical form), or its codec is not available.</exception>
    /// <exception cref="AvroDataException">An input is malformed.</exception>
    public static void Concat(Stream output, IEnumerable<Stream> inputs, AvroFileReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(inputs);
        var streams = inputs.ToList();
        if (streams.Count == 0)
        {
            throw new ArgumentException("There are no files to concatenate.", nameof(inputs));
        }

        options ??= AvroFileReaderOptions.Default;
        var readerOptions = new AvroFileReaderOptions
        {
            Codecs = options.Codecs,
            MaxBlockLength = options.MaxBlockLength,
            MaxSchemaLength = options.MaxSchemaLength,
            MaxZeroSizeValuesPerBlock = options.MaxZeroSizeValuesPerBlock,
            LeaveOpen = true,
        };

        if (streams.IndexOf(null!) is var missing and >= 0)
        {
            throw new ArgumentException($"Input {missing} is null.", nameof(inputs));
        }

        using var writer = CreateFromFirst(streams[0], output, readerOptions);
        for (var i = 1; i < streams.Count; i++)
        {
            CopyBlocks(streams[i], i, readerOptions, writer);
        }
    }

    // A writer with the first input's schema, codec and metadata, holding its blocks.
    private static AvroFileWriter<AvroValue> CreateFromFirst(Stream input, Stream output, AvroFileReaderOptions readerOptions)
    {
        using var reader = AvroFileReader.OpenGeneric(input, options: readerOptions);
        var writer = AvroFileWriter.CreateGeneric(output, reader.WriterSchema, new AvroFileWriterOptions
        {
            Codec = reader.Codec,
            Metadata = UserMetadata(reader.Metadata),
            LeaveOpen = true,
        });
        try
        {
            CopyBlocks(reader, writer, readerOptions);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static void CopyBlocks(Stream input, int index, AvroFileReaderOptions readerOptions, AvroFileWriter<AvroValue> writer)
    {
        using var reader = AvroFileReader.OpenGeneric(input, options: readerOptions);
        if (!string.Equals(reader.WriterSchema.CanonicalForm, writer.Schema.CanonicalForm, StringComparison.Ordinal))
        {
            throw new AvroException($"Input {index}'s schema is {reader.WriterSchema.CanonicalForm}, not the first input's {writer.Schema.CanonicalForm}.");
        }

        CopyBlocks(reader, writer, readerOptions);
    }

    private static void CopyBlocks(AvroFileReader<AvroValue> reader, AvroFileWriter<AvroValue> writer, AvroFileReaderOptions readerOptions)
    {
        while (reader.TryReadBlock(out var block))
        {
            writer.WriteBlockWithLimit(block, recompress: false, readerOptions.MaxBlockLength);
        }
    }

    // The entries an application added: the avro.* ones belong to the writer.
    private static Dictionary<string, ReadOnlyMemory<byte>>? UserMetadata(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> metadata)
    {
        var user = metadata
            .Where(e => !e.Key.StartsWith(AvroContainerFormat.ReservedPrefix, StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return user.Count == 0 ? null : user;
    }
}
