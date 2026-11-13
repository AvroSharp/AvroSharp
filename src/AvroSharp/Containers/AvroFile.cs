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

        AvroFileWriter<AvroValue>? writer = null;
        try
        {
            for (var i = 0; i < streams.Count; i++)
            {
                var input = streams[i] ?? throw new ArgumentException($"Input {i} is null.", nameof(inputs));
                writer = CopyBlocks(input, i, output, readerOptions, writer);
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    // Copies one input's blocks; the first input creates the writer, with its schema, codec and metadata.
    private static AvroFileWriter<AvroValue> CopyBlocks(Stream input, int index, Stream output, AvroFileReaderOptions readerOptions, AvroFileWriter<AvroValue>? writer)
    {
        using (var reader = AvroFileReader.OpenGeneric(input, options: readerOptions))
        {
            if (writer is null)
            {
                writer = AvroFileWriter.CreateGeneric(output, reader.WriterSchema, new AvroFileWriterOptions
                {
                    Codec = reader.Codec,
                    Metadata = UserMetadata(reader.Metadata),
                    LeaveOpen = true,
                });
            }
            else if (!string.Equals(reader.WriterSchema.CanonicalForm, writer.Schema.CanonicalForm, StringComparison.Ordinal))
            {
                throw new AvroException($"Input {index}'s schema is {reader.WriterSchema.CanonicalForm}, not the first input's {writer.Schema.CanonicalForm}.");
            }

            while (reader.TryReadBlock(out var block))
            {
                writer.WriteBlock(block);
            }
        }

        return writer;
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
