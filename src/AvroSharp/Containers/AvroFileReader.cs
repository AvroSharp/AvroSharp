using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Containers;

/// <summary>Opens <see cref="AvroFileReader{T}"/> instances.</summary>
public static class AvroFileReader
{
    /// <summary>Reads a file's header and returns a reader of its objects.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The source, positioned at the start of the file.</param>
    /// <param name="createReader">
    /// Called once with the file's schema; returns the function that reads one object. For a generated type whose
    /// schema may differ from the file's, return a function calling its <c>Read(ref reader, writerSchema)</c>.
    /// </param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="AvroDataException">The header is malformed.</exception>
    /// <exception cref="AvroException">The file's codec is not available.</exception>
    public static AvroFileReader<T> Open<T>(Stream stream, Func<AvroSchema, AvroReadFunc<T>> createReader, AvroFileReaderOptions? options = null)
    {
        var reader = Create(stream, createReader, options);
        try
        {
            reader.ReadHeader();
            reader.SetReader(createReader(reader.WriterSchema) ?? throw new InvalidOperationException("createReader returned null."));
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Reads a file's header asynchronously and returns a reader of its objects.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The source, positioned at the start of the file.</param>
    /// <param name="createReader">
    /// Called once with the file's schema; returns the function that reads one object (see
    /// <see cref="Open{T}(Stream, Func{AvroSchema, AvroReadFunc{T}}, AvroFileReaderOptions?)"/>).
    /// </param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="AvroDataException">The header is malformed.</exception>
    /// <exception cref="AvroException">The file's codec is not available.</exception>
    public static async ValueTask<AvroFileReader<T>> OpenAsync<T>(Stream stream, Func<AvroSchema, AvroReadFunc<T>> createReader, AvroFileReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reader = Create(stream, createReader, options);
        try
        {
            await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            reader.SetReader(createReader(reader.WriterSchema) ?? throw new InvalidOperationException("createReader returned null."));
            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a file's header and returns a reader of its objects as generic values.</summary>
    /// <param name="stream">The source, positioned at the start of the file.</param>
    /// <param name="readerSchema">
    /// The schema to read the objects as, resolved against the file's schema; <see langword="null"/> reads them as written.
    /// </param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <param name="readerOptions">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    public static AvroFileReader<AvroValue> OpenGeneric(Stream stream, AvroSchema? readerSchema = null, AvroFileReaderOptions? options = null, GenericDatumReaderOptions? readerOptions = null) =>
        Open(stream, GenericReader(readerSchema, readerOptions), options);

    /// <summary>Reads a file's header asynchronously and returns a reader of its objects as generic values.</summary>
    /// <param name="stream">The source, positioned at the start of the file.</param>
    /// <param name="readerSchema">
    /// The schema to read the objects as, resolved against the file's schema; <see langword="null"/> reads them as written.
    /// </param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <param name="readerOptions">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    public static ValueTask<AvroFileReader<AvroValue>> OpenGenericAsync(Stream stream, AvroSchema? readerSchema = null, AvroFileReaderOptions? options = null, GenericDatumReaderOptions? readerOptions = null, CancellationToken cancellationToken = default) =>
        OpenAsync(stream, GenericReader(readerSchema, readerOptions), options, cancellationToken);

    private static AvroFileReader<T> Create<T>(Stream stream, Func<AvroSchema, AvroReadFunc<T>> createReader, AvroFileReaderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(createReader);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream is not readable.", nameof(stream));
        }

        return new AvroFileReader<T>(stream, options ?? AvroFileReaderOptions.Default);
    }

    private static Func<AvroSchema, AvroReadFunc<AvroValue>> GenericReader(AvroSchema? readerSchema, GenericDatumReaderOptions? readerOptions) =>
        writerSchema =>
        {
            var datumReader = readerSchema is null
                ? GenericDatumReader.Create(writerSchema, readerOptions)
                : GenericDatumReader.Create(writerSchema, readerSchema, readerOptions);
            return (ref AvroReader reader) => datumReader.Read(ref reader);
        };
}
