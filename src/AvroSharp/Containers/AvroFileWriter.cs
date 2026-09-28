using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Containers;

/// <summary>Creates <see cref="AvroFileWriter{T}"/> instances.</summary>
public static class AvroFileWriter
{
    /// <summary>Creates a writer. The header is written with the first block, or when the writer is flushed or disposed.</summary>
    /// <typeparam name="T">The type of the objects.</typeparam>
    /// <param name="stream">The destination.</param>
    /// <param name="schema">The schema of every object in the file.</param>
    /// <param name="write">
    /// Writes one object in <paramref name="schema"/>'s encoding; for a generated type, its static <c>Write</c> method.
    /// </param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">The metadata uses a reserved key.</exception>
    public static AvroFileWriter<T> Create<T>(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(write);
        options ??= AvroFileWriterOptions.Default;
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(stream));
        }

        return new AvroFileWriter<T>(stream, schema, write, options);
    }

    /// <summary>Creates a writer of generic values (<see cref="AvroValue"/>, <see cref="GenericRecord"/>).</summary>
    /// <param name="stream">The destination.</param>
    /// <param name="schema">The schema of every object in the file.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <param name="writerOptions">The generic writer's options, or <see langword="null"/> for the defaults.</param>
    public static AvroFileWriter<AvroValue> CreateGeneric(Stream stream, AvroSchema schema, AvroFileWriterOptions? options = null, GenericDatumWriterOptions? writerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var datumWriter = GenericDatumWriter.Create(schema, writerOptions);
        return Create<AvroValue>(stream, schema, (ref writer, value) => datumWriter.Write(ref writer, value), options);
    }
}

/// <summary>
/// Writes an Avro object container file: a header holding the schema, then blocks of objects, each compressed with
/// the file's codec and followed by the sync marker. Objects are buffered until a block reaches
/// <see cref="AvroFileWriterOptions.SyncInterval"/>; <see cref="Flush"/> and <see cref="Dispose"/> write the rest.
/// The header is written with the first block, or when the writer is flushed or disposed. The asynchronous members
/// (<see cref="WriteAsync"/>, <see cref="FlushAsync"/>, <see cref="DisposeAsync"/>) do no synchronous I/O.
/// </summary>
/// <typeparam name="T">The type of the objects.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class AvroFileWriter<T> : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AvroWriteAction<T> _write;
    private readonly AvroCodec _codec;
    private readonly int _syncInterval;
    private readonly bool _leaveOpen;
    // A block's count and size (two varints, at most 10 bytes each) are written right-aligned into this much room
    // left at the start of the block's buffer, and the sync marker after its data, so each block is one write.
    private const int PrefixRoom = 20;

    private readonly byte[] _sync = new byte[AvroContainerFormat.SyncSize];
    private readonly PooledBufferWriter _block;
    private PooledBufferWriter? _compressed;
    private byte[]? _pendingHeader;
    private long _blockCount;
    private bool _disposed;

    internal AvroFileWriter(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions options)
    {
        _stream = stream;
        Schema = schema;
        _write = write;
        _codec = options.Codec ?? AvroCodec.Null;
        _syncInterval = options.SyncInterval;
        _leaveOpen = options.LeaveOpen;
        _pendingHeader = BuildHeader(options);

        // Sized for a whole block at the sync interval (up to 1 MiB), so a typical block needs no growth.
        _block = new PooledBufferWriter(PrefixRoom + Math.Min(_syncInterval, 1024 * 1024) + 1024);
        ClearBlock();
    }

    /// <summary>Gets the schema of the objects.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Appends one object, writing a block when the buffered objects reach the sync interval.</summary>
    /// <param name="value">The object.</param>
    /// <remarks>If writing the object throws, nothing of it is kept and the writer stays usable.</remarks>
    public void Write(T value)
    {
        if (Append(value))
        {
            WriteBlock();
        }
    }

    /// <summary>
    /// Appends one object; when the buffered objects reach the sync interval, the block is written to the stream
    /// asynchronously. The object itself is encoded synchronously, into memory.
    /// </summary>
    /// <param name="value">The object.</param>
    /// <param name="cancellationToken">Cancels writing a block.</param>
    /// <remarks>If encoding the object throws, nothing of it is kept and the writer stays usable.</remarks>
    public ValueTask WriteAsync(T value, CancellationToken cancellationToken = default) =>
        Append(value) ? WriteBlockAsync(cancellationToken) : default;

    /// <summary>Writes the buffered objects as a block, if there are any, and flushes the stream.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteBlock();
        _stream.Flush();
    }

    /// <summary>Writes the buffered objects as a block, if there are any, and flushes the stream, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await WriteBlockAsync(cancellationToken).ConfigureAwait(false);
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
            WriteBlock();
            _stream.Flush();
        }
        finally
        {
            ReleaseBuffers();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes the buffered objects, flushes the stream and, unless the options say otherwise, disposes it, all
    /// asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await WriteBlockAsync(CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseBuffers();
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

    // Encodes one object into the block; returns whether the block is now due.
    private bool Append(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var start = _block.WrittenCount;
        try
        {
            var writer = new AvroWriter(_block);
            _write(ref writer, value);
            writer.Flush();
        }
        catch
        {
            _block.Truncate(start);
            throw;
        }

        _blockCount++;
        return _block.WrittenCount - PrefixRoom >= _syncInterval;
    }

    private void ReleaseBuffers()
    {
        _disposed = true;
        _block.Dispose();
        _compressed?.Dispose();
    }

    private byte[] BuildHeader(AvroFileWriterOptions options)
    {
        var metadata = options.Metadata;
        if (metadata is not null)
        {
            foreach (var entry in metadata)
            {
                if (entry.Key is null || entry.Key.StartsWith(AvroContainerFormat.ReservedPrefix, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Metadata key '{entry.Key}' is reserved: keys beginning with '{AvroContainerFormat.ReservedPrefix}' belong to the specification.", nameof(options));
                }

                ArgumentNullException.ThrowIfNull(entry.Value, nameof(options));
            }
        }

        FillSyncMarker(_sync);
        using var header = new PooledBufferWriter(1024);
        var writer = new AvroWriter(header);
        writer.WriteRaw(AvroContainerFormat.Magic);
        writer.WriteBlockCount(2 + (metadata?.Count ?? 0));
        writer.WriteString(AvroContainerFormat.SchemaKey);
        // A string and bytes have the same encoding: a length and the UTF-8 bytes.
        writer.WriteString(Schema.ToJson());
        writer.WriteString(AvroContainerFormat.CodecKey);
        writer.WriteString(_codec.Name);
        if (metadata is not null)
        {
            foreach (var entry in metadata)
            {
                writer.WriteString(entry.Key);
                writer.WriteBytes(entry.Value);
            }
        }

        writer.WriteBlockEnd();
        writer.WriteRaw(_sync);
        writer.Flush();
        return header.ToArray();
    }

    private void WriteBlock()
    {
        if (_pendingHeader is { } header)
        {
            _stream.Write(header, 0, header.Length);
            _pendingHeader = null;
        }

        if (_blockCount == 0)
        {
            return;
        }

        var output = FrameBlock(out var block);
        try
        {
            _stream.Write(block.Array!, block.Offset, block.Count);
        }
        finally
        {
            Unframe(output);
        }

        ClearBlock();
    }

    private async ValueTask WriteBlockAsync(CancellationToken cancellationToken)
    {
        if (_pendingHeader is { } header)
        {
            await WriteToStreamAsync(new ArraySegment<byte>(header), cancellationToken).ConfigureAwait(false);
            _pendingHeader = null;
        }

        if (_blockCount == 0)
        {
            return;
        }

        var output = FrameBlock(out var block);
        try
        {
            await WriteToStreamAsync(block, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Unframe(output);
        }

        ClearBlock();
    }

    private ValueTask WriteToStreamAsync(ArraySegment<byte> data, CancellationToken cancellationToken) =>
#if NETSTANDARD2_0
        new(_stream.WriteAsync(data.Array!, data.Offset, data.Count, cancellationToken));
#else
        _stream.WriteAsync(data.AsMemory(), cancellationToken);
#endif

    // Compresses the buffered objects when the codec is not null, then frames them in place: the count and size
    // right-aligned into the room before the data, and the sync marker after it. Returns the buffer that holds the
    // frame; `block` is the whole block as written to the stream.
    private PooledBufferWriter FrameBlock(out ArraySegment<byte> block)
    {
        PooledBufferWriter output;
        if (ReferenceEquals(_codec, AvroCodec.Null))
        {
            output = _block;
        }
        else
        {
            output = _compressed ??= new PooledBufferWriter(_block.WrittenCount + AvroContainerFormat.SyncSize);
            output.Clear();
            output.Reserve(PrefixRoom);
            _codec.Compress(_block.WrittenMemory[PrefixRoom..], output);
        }

        var size = output.WrittenCount - PrefixRoom;
        Span<byte> prefix = stackalloc byte[PrefixRoom];
        var writer = new AvroWriter(prefix);
        writer.WriteLong(_blockCount);
        writer.WriteLong(size);
        var prefixLength = (int)writer.BytesWritten;

        output.Write(_sync);
        var frame = output.WrittenSegment;
        var start = PrefixRoom - prefixLength;
        prefix[..prefixLength].CopyTo(frame.AsSpan(start));
        block = new ArraySegment<byte>(frame.Array!, start, frame.Count - start);
        return output;
    }

    // Removes the sync marker that FrameBlock appended, so a block whose write failed is not framed twice.
    private static void Unframe(PooledBufferWriter output) => output.Truncate(output.WrittenCount - AvroContainerFormat.SyncSize);

    private void ClearBlock()
    {
        _block.Clear();
        _block.Reserve(PrefixRoom);
        _blockCount = 0;
    }

    private static void FillSyncMarker(byte[] sync)
    {
#if NETSTANDARD2_0
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(sync);
#else
        RandomNumberGenerator.Fill(sync);
#endif
    }
}
