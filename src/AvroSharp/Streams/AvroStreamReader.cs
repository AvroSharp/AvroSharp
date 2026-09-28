using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Streams;

/// <summary>Opens <see cref="AvroStreamReader{T}"/> instances.</summary>
public static class AvroStreamReader
{
    /// <summary>Creates a reader of the objects in a stream, each in the binary encoding of one schema, one after another.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The source.</param>
    /// <param name="read">Reads one object; for a generated type, its static <c>Read</c> method.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for the defaults.</param>
    public static AvroStreamReader<T> Open<T>(Stream stream, AvroReadFunc<T> read, AvroStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(read);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream is not readable.", nameof(stream));
        }

        return new AvroStreamReader<T>(stream, read, options ?? AvroStreamOptions.Default);
    }

    /// <summary>Creates a reader of the objects in a stream as generic values.</summary>
    /// <param name="stream">The source.</param>
    /// <param name="writerSchema">The schema the objects were written with; a stream does not record it.</param>
    /// <param name="readerSchema">The schema to read the objects as; <see langword="null"/> reads them as written.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for the defaults.</param>
    /// <param name="readerOptions">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    public static AvroStreamReader<AvroValue> OpenGeneric(Stream stream, AvroSchema writerSchema, AvroSchema? readerSchema = null, AvroStreamOptions? options = null, GenericDatumReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        var datumReader = readerSchema is null
            ? GenericDatumReader.Create(writerSchema, readerOptions)
            : GenericDatumReader.Create(writerSchema, readerSchema, readerOptions);
        return Open(stream, datumReader.Read, options);
    }
}

/// <summary>
/// Reads objects written one after another in the binary encoding, with no container, framing or lengths, until the
/// stream ends: data from a socket or a pipe, or a file of concatenated objects. The objects' schema is not in the
/// stream; the caller supplies it. See <see cref="AvroStreamWriter{T}"/> for writing such a stream.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the encoding marks where an object ends, so each is decoded to find its end. When the buffered data
/// ends inside an object, more is read and the object is decoded again from its start. Only when the stream has
/// ended, or <see cref="AvroStreamOptions.MaxDatumLength"/> bytes are buffered, is a failure reported: truncated and
/// corrupt data look alike until then. Objects that encode to no bytes (a <c>null</c> schema, an empty record) cannot
/// be delimited, and reading one is an error.
/// </para>
/// <para>The asynchronous members do no synchronous I/O; each object is decoded synchronously once it is in memory. Instances are not thread-safe.</para>
/// </remarks>
/// <typeparam name="T">The type of the objects.</typeparam>
public sealed class AvroStreamReader<T> : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AvroReadFunc<T> _read;
    private readonly int _maxDatumLength;
    private readonly bool _leaveOpen;

    // The unread bytes are _buffer[_start.._end]; _position is the stream offset of _start.
    private byte[] _buffer;
    private int _start;
    private int _end;
    private long _position;
    private bool _streamEnded;
    private bool _disposed;

    internal AvroStreamReader(Stream stream, AvroReadFunc<T> read, AvroStreamOptions options)
    {
        _stream = stream;
        _read = read;
        _maxDatumLength = options.MaxDatumLength;
        _leaveOpen = options.LeaveOpen;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(options.BufferSize, options.MaxDatumLength));
    }

    private enum Step
    {
        Read,
        End,
        NeedMore,
    }

    /// <summary>Gets the number of bytes of the stream that the objects read so far took.</summary>
    public long Position => _position;

    /// <summary>Reads the next object.</summary>
    /// <param name="value">The object, when there is one.</param>
    /// <returns><see langword="false"/> at the end of the stream.</returns>
    /// <exception cref="AvroDataException">The data is malformed, ends inside an object, or an object is larger than the limit.</exception>
    public bool TryRead([MaybeNullWhen(false)] out T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            switch (TryDecode(out value))
            {
                case Step.Read:
                    return true;
                case Step.End:
                    return false;
                default:
                    Fill();
                    break;
            }
        }
    }

    /// <summary>Reads the objects to the end of the stream.</summary>
    public IEnumerable<T> ReadAll()
    {
        while (TryRead(out var value))
        {
            yield return value;
        }
    }

    /// <summary>Reads the objects to the end of the stream asynchronously.</summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (TryDecode(out var value))
            {
                case Step.Read:
                    yield return value;
                    break;
                case Step.End:
                    yield break;
                default:
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Releases the buffer and, unless the options say otherwise, disposes the stream.</summary>
    public void Dispose()
    {
        if (Release() && !_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Releases the buffer and, unless the options say otherwise, disposes the stream asynchronously.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Release() && !_leaveOpen)
        {
#if NETSTANDARD2_0
            _stream.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
#else
            await _stream.DisposeAsync().ConfigureAwait(false);
#endif
        }
    }

    private Step TryDecode(out T value)
    {
        value = default!;
        var buffered = _end - _start;
        if (buffered == 0)
        {
            return _streamEnded ? Step.End : Step.NeedMore;
        }

        var reader = new AvroReader(_buffer.AsSpan(_start, buffered));
        try
        {
            value = _read(ref reader);
        }
        catch (AvroDataException) when (!_streamEnded && buffered < _maxDatumLength)
        {
            // The object may only be cut off by the end of the buffer: read more and decode it again.
            return Step.NeedMore;
        }
        catch (AvroDataException ex)
        {
            throw Unreadable(ex, buffered);
        }

        var consumed = (int)reader.BytesConsumed;
        if (consumed == 0)
        {
            throw new AvroDataException("An object that encodes to no bytes cannot be read from a stream: nothing marks where one ends and the next begins.");
        }

        _start += consumed;
        _position += consumed;
        return Step.Read;
    }

    private AvroDataException Unreadable(AvroDataException ex, int buffered) => _streamEnded
        ? new($"The object at stream offset {_position} cannot be read: {ex.Message}", ex)
        : new($"The object at stream offset {_position} is not complete within {buffered} bytes, the limit (AvroStreamOptions.MaxDatumLength): {ex.Message}", ex);

    private void Fill()
    {
        var room = PrepareFill();
        var read = _stream.Read(_buffer, _end, room);
        Filled(read);
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        var room = PrepareFill();
#if NETSTANDARD2_0
        var read = await _stream.ReadAsync(_buffer, _end, room, cancellationToken).ConfigureAwait(false);
#else
        var read = await _stream.ReadAsync(_buffer.AsMemory(_end, room), cancellationToken).ConfigureAwait(false);
#endif
        Filled(read);
    }

    // Moves the unread bytes to the start of the buffer, growing it (up to the limit) when they fill it; returns the room to read into.
    private int PrepareFill()
    {
        var buffered = _end - _start;
        var capacity = Math.Min(_buffer.Length, _maxDatumLength);
        if (buffered == capacity)
        {
            var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)capacity * 2, _maxDatumLength));
            _buffer.AsSpan(_start, buffered).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
            capacity = Math.Min(_buffer.Length, _maxDatumLength);
        }
        else if (_start > 0)
        {
            _buffer.AsSpan(_start, buffered).CopyTo(_buffer);
        }

        _start = 0;
        _end = buffered;
        return capacity - buffered;
    }

    private void Filled(int read)
    {
        if (read == 0)
        {
            _streamEnded = true;
        }
        else
        {
            _end += read;
        }
    }

    private bool Release()
    {
        if (_disposed)
        {
            return false;
        }

        _disposed = true;
        var buffer = _buffer;
        _buffer = [];
        ArrayPool<byte>.Shared.Return(buffer);
        return true;
    }
}
