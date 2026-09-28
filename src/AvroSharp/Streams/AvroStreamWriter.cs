using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Streams;

/// <summary>Creates <see cref="AvroStreamWriter{T}"/> instances.</summary>
public static class AvroStreamWriter
{
    /// <summary>Creates a writer of objects one after another in the binary encoding, with no container or framing.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The destination.</param>
    /// <param name="write">Writes one object; for a generated type, its static <c>Write</c> method.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for the defaults.</param>
    public static AvroStreamWriter<T> Create<T>(Stream stream, AvroWriteAction<T> write, AvroStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(write);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(stream));
        }

        return new AvroStreamWriter<T>(stream, write, options ?? AvroStreamOptions.Default);
    }

    /// <summary>Creates a writer of generic values (<see cref="AvroValue"/>, <see cref="GenericRecord"/>).</summary>
    /// <param name="stream">The destination.</param>
    /// <param name="schema">The schema of every object.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">The generic writer's options, or <see langword="null"/> for the defaults.</param>
    public static AvroStreamWriter<AvroValue> CreateGeneric(Stream stream, AvroSchema schema, AvroStreamOptions? options = null, GenericDatumWriterOptions? writerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var datumWriter = GenericDatumWriter.Create(schema, writerOptions);
        return Create<AvroValue>(stream, (ref writer, value) => datumWriter.Write(ref writer, value), options);
    }

#if NET8_0_OR_GREATER
    /// <summary>Creates a writer of objects of a generated type, one after another.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="stream">The destination.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for the defaults.</param>
    public static AvroStreamWriter<T> Create<T>(Stream stream, AvroStreamOptions? options = null)
        where T : IAvroSerializable<T> =>
        Create(stream, AvroSerializable<T>.Write, options);
#endif
}

/// <summary>
/// Writes objects one after another in the binary encoding, with no container, framing or lengths, for
/// <see cref="AvroStreamReader{T}"/> or any reader that knows the schema. Objects are buffered and written to the
/// stream once <see cref="AvroStreamOptions.BufferSize"/> bytes are buffered, and on <see cref="Flush"/> and
/// <see cref="Dispose"/>. The asynchronous members do no synchronous I/O.
/// </summary>
/// <remarks>
/// Use an object container file instead when the data should carry its schema, be compressed, or be split. Instances
/// are not thread-safe.
/// </remarks>
/// <typeparam name="T">The type of the objects.</typeparam>
public sealed class AvroStreamWriter<T> : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AvroWriteAction<T> _write;
    private readonly int _bufferSize;
    private readonly bool _leaveOpen;
    private readonly PooledBufferWriter _buffer;
    private bool _disposed;

    internal AvroStreamWriter(Stream stream, AvroWriteAction<T> write, AvroStreamOptions options)
    {
        _stream = stream;
        _write = write;
        _bufferSize = options.BufferSize;
        _leaveOpen = options.LeaveOpen;
        _buffer = new PooledBufferWriter(_bufferSize + 1024);
    }

    /// <summary>Appends one object, writing the buffer to the stream once it is full.</summary>
    /// <param name="value">The object.</param>
    /// <remarks>If writing the object throws, nothing of it is kept and the writer stays usable.</remarks>
    public void Write(T value)
    {
        if (Append(value))
        {
            WriteBuffer();
        }
    }

    /// <summary>Appends one object; once the buffer is full, it is written to the stream asynchronously.</summary>
    /// <param name="value">The object.</param>
    /// <param name="cancellationToken">Cancels writing the buffer.</param>
    /// <remarks>If encoding the object throws, nothing of it is kept and the writer stays usable.</remarks>
    public ValueTask WriteAsync(T value, CancellationToken cancellationToken = default) =>
        Append(value) ? WriteBufferAsync(cancellationToken) : default;

    /// <summary>Writes the buffered objects and flushes the stream.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteBuffer();
        _stream.Flush();
    }

    /// <summary>Writes the buffered objects and flushes the stream, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await WriteBufferAsync(cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the buffered objects, flushes the stream and, unless the options say otherwise, disposes it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            WriteBuffer();
            _stream.Flush();
        }
        finally
        {
            Release();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <summary>Writes the buffered objects, flushes the stream and, unless the options say otherwise, disposes it, all asynchronously.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await WriteBufferAsync(CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            Release();
            if (!_leaveOpen)
            {
#if NETSTANDARD2_0
                _stream.Dispose();
#else
                await _stream.DisposeAsync().ConfigureAwait(false);
#endif
            }
        }
    }

    // Encodes one object into the buffer; returns whether the buffer is now due.
    private bool Append(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var start = _buffer.WrittenCount;
        try
        {
            var writer = new AvroWriter(_buffer);
            _write(ref writer, value);
            writer.Flush();
        }
        catch
        {
            _buffer.Truncate(start);
            throw;
        }

        return _buffer.WrittenCount >= _bufferSize;
    }

    private void WriteBuffer()
    {
        if (_buffer.WrittenCount > 0)
        {
            var data = _buffer.WrittenSegment;
            _stream.Write(data.Array!, data.Offset, data.Count);
            _buffer.Clear();
        }
    }

    private async ValueTask WriteBufferAsync(CancellationToken cancellationToken)
    {
        if (_buffer.WrittenCount > 0)
        {
            var data = _buffer.WrittenSegment;
#if NETSTANDARD2_0
            await _stream.WriteAsync(data.Array!, data.Offset, data.Count, cancellationToken).ConfigureAwait(false);
#else
            await _stream.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
#endif
            _buffer.Clear();
        }
    }

    private void Release()
    {
        _disposed = true;
        _buffer.Dispose();
    }
}
