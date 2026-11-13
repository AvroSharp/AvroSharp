using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Containers;

/// <summary>Appending to an existing file.</summary>
public static partial class AvroFileWriter
{
    /// <summary>
    /// Opens a container file to append to: reads its header, then writes new blocks at its end with the file's sync
    /// marker, so the file reads as one. An empty stream becomes a new file, as <see cref="Create{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/> writes it.
    /// </summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="schema">The schema of the objects; it must be the file's (the same canonical form).</param>
    /// <param name="write">Writes one object in <paramref name="schema"/>'s encoding.</param>
    /// <param name="options">
    /// The file options, or <see langword="null"/> for the defaults. <see cref="AvroFileWriterOptions.Codec"/> must be the
    /// file's codec; the default, the null codec, also stands for the file's when that is null or deflate. Metadata
    /// can't be given: the header is already written.
    /// </param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, or the options have metadata.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the writer's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static AvroFileWriter<T> OpenAppend<T>(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(write);
        options = CheckAppendable(stream, options);
        return stream.Length == 0
            ? new AvroFileWriter<T>(stream, schema, write, options)
            : Append(stream, schema, write, options, ReadAppendHeader(stream));
    }

    /// <summary>Opens a container file to append to, as <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/> does, reading its header asynchronously.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="schema">The schema of the objects; it must be the file's (the same canonical form).</param>
    /// <param name="write">Writes one object in <paramref name="schema"/>'s encoding.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults; see <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, or the options have metadata.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the writer's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static async ValueTask<AvroFileWriter<T>> OpenAppendAsync<T>(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(write);
        options = CheckAppendable(stream, options);
        return stream.Length == 0
            ? new AvroFileWriter<T>(stream, schema, write, options)
            : Append(stream, schema, write, options, await ReadAppendHeaderAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Opens a container file to append generic values to, as <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>
    /// does. Without a schema, the file's own is used.
    /// </summary>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="schema">The schema of the values, or <see langword="null"/> for the file's; an empty stream needs one.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults; see <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>.</param>
    /// <param name="writerOptions">The generic writer's options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, the options have metadata, or the stream is empty and no schema is given.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the writer's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static AvroFileWriter<AvroValue> OpenAppendGeneric(Stream stream, AvroSchema? schema = null, AvroFileWriterOptions? options = null, GenericDatumWriterOptions? writerOptions = null)
    {
        options = CheckAppendable(stream, options);
        return stream.Length == 0
            ? CreateGeneric(stream, schema ?? throw EmptyNeedsSchema(nameof(schema)), options, writerOptions)
            : AppendGeneric(stream, schema, options, writerOptions, ReadAppendHeader(stream));
    }

    /// <summary>Opens a container file to append generic values to, as <see cref="OpenAppendGeneric"/> does, reading its header asynchronously.</summary>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="schema">The schema of the values, or <see langword="null"/> for the file's; an empty stream needs one.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults; see <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>.</param>
    /// <param name="writerOptions">The generic writer's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, the options have metadata, or the stream is empty and no schema is given.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the writer's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static async ValueTask<AvroFileWriter<AvroValue>> OpenAppendGenericAsync(Stream stream, AvroSchema? schema = null, AvroFileWriterOptions? options = null, GenericDatumWriterOptions? writerOptions = null, CancellationToken cancellationToken = default)
    {
        options = CheckAppendable(stream, options);
        return stream.Length == 0
            ? CreateGeneric(stream, schema ?? throw EmptyNeedsSchema(nameof(schema)), options, writerOptions)
            : AppendGeneric(stream, schema, options, writerOptions, await ReadAppendHeaderAsync(stream, cancellationToken).ConfigureAwait(false));
    }

#if NET8_0_OR_GREATER
    /// <summary>Opens a container file of a generated type to append to, as <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/> does.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults; see <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>.</param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, or the options have metadata.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the type's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static AvroFileWriter<T> OpenAppend<T>(Stream stream, AvroFileWriterOptions? options = null)
        where T : IAvroSerializable<T> =>
        OpenAppend(stream, T.Schema, AvroSerializable<T>.Write, options);

    /// <summary>Opens a container file of a generated type to append to, reading its header asynchronously.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="stream">The file: readable, writable and seekable.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults; see <see cref="OpenAppend{T}(Stream, AvroSchema, AvroWriteAction{T}, AvroFileWriterOptions?)"/>.</param>
    /// <param name="cancellationToken">Cancels reading the header.</param>
    /// <exception cref="ArgumentException">The stream can't be read, written and seeked, or the options have metadata.</exception>
    /// <exception cref="AvroException">The file's schema or codec is not the type's.</exception>
    /// <exception cref="AvroDataException">The file's header is malformed.</exception>
    public static ValueTask<AvroFileWriter<T>> OpenAppendAsync<T>(Stream stream, AvroFileWriterOptions? options = null, CancellationToken cancellationToken = default)
        where T : IAvroSerializable<T> =>
        OpenAppendAsync(stream, T.Schema, AvroSerializable<T>.Write, options, cancellationToken);
#endif

    private static AvroFileWriterOptions CheckAppendable(Stream stream, AvroFileWriterOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("Appending needs a stream that can be read, written and seeked.", nameof(stream));
        }

        options ??= AvroFileWriterOptions.Default;
        if (options.Metadata is not null)
        {
            throw new ArgumentException("Metadata can't be added when appending: the file's header is already written.", nameof(options));
        }

        return options;
    }

    private static ArgumentException EmptyNeedsSchema(string paramName) =>
        new("The stream is empty, so it has no schema to append with; give one.", paramName);

    private static AppendHeader ReadAppendHeader(Stream stream)
    {
        stream.Position = 0;
        using var reader = new AvroFileReader<AvroValue>(stream, new AvroFileReaderOptions { LeaveOpen = true }, requireCodec: false);
        reader.ReadHeader();
        return new AppendHeader(reader.WriterSchema, reader.CodecName, reader.SyncMarker.ToArray());
    }

    private static async ValueTask<AppendHeader> ReadAppendHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var reader = new AvroFileReader<AvroValue>(stream, new AvroFileReaderOptions { LeaveOpen = true }, requireCodec: false);
        try
        {
            await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            return new AppendHeader(reader.WriterSchema, reader.CodecName, reader.SyncMarker.ToArray());
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static AvroFileWriter<AvroValue> AppendGeneric(Stream stream, AvroSchema? schema, AvroFileWriterOptions options, GenericDatumWriterOptions? writerOptions, AppendHeader header)
    {
        schema ??= header.Schema;
        var datumWriter = GenericDatumWriter.Create(schema, writerOptions);
        return Append<AvroValue>(stream, schema, (ref writer, value) => datumWriter.Write(ref writer, value), options, header);
    }

    private static AvroFileWriter<T> Append<T>(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions options, AppendHeader header)
    {
        if (!ReferenceEquals(schema, header.Schema)
            && !string.Equals(schema.CanonicalForm, header.Schema.CanonicalForm, StringComparison.Ordinal))
        {
            throw new AvroException($"The file's schema is {header.Schema.CanonicalForm}, not the writer's {schema.CanonicalForm}; appending needs the same schema.");
        }

        // The options' codec when it is the file's; the default (null) codec also stands for a built-in one.
        var codec = string.Equals(options.Codec.Name, header.CodecName, StringComparison.Ordinal)
            ? options.Codec
            : ReferenceEquals(options.Codec, AvroCodec.Null) && AvroFileReader<AvroValue>.FindCodec(header.CodecName, []) is { } builtIn
                ? builtIn
                : throw new AvroException($"The file is compressed with the '{header.CodecName}' codec, not the writer's '{options.Codec.Name}'; set AvroFileWriterOptions.Codec to the file's codec.");

        stream.Seek(0, SeekOrigin.End);
        return new AvroFileWriter<T>(stream, schema, write, options, codec, header.Sync);
    }

    private sealed record AppendHeader(AvroSchema Schema, string CodecName, byte[] Sync);
}
