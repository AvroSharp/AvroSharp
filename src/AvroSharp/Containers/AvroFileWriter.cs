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
/// <seealso cref="AvroFileWriterOptions"/>
/// <seealso cref="AvroFileReader"/>
/// <seealso cref="AvroCodec"/>
public static partial class AvroFileWriter
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

#if NET8_0_OR_GREATER
    /// <summary>Creates a container file of a generated type, with its schema.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="stream">The destination.</param>
    /// <param name="options">The file options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">The metadata uses a reserved key.</exception>
    public static AvroFileWriter<T> Create<T>(Stream stream, AvroFileWriterOptions? options = null)
        where T : IAvroSerializable<T> =>
        Create(stream, T.Schema, AvroSerializable<T>.Write, options);
#endif
}

/// <summary>
/// Writes an Avro object container file: a header holding the schema, then blocks of objects, each compressed with
/// the file's codec and followed by the sync marker. Objects are buffered until a block reaches
/// <see cref="AvroFileWriterOptions.SyncInterval"/> or 65,536 objects; <see cref="Flush"/> and <see cref="Dispose"/>
/// write the rest.
/// The header is written with the first block, or when the writer is flushed or disposed. The asynchronous members
/// (<see cref="WriteAsync"/>, <see cref="FlushAsync"/>, <see cref="DisposeAsync"/>) do no synchronous I/O.
/// </summary>
/// <typeparam name="T">The type of the objects.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
/// <seealso cref="AvroFileWriter"/>
/// <seealso cref="AvroFileWriterOptions"/>
/// <seealso cref="AvroFileReader{T}"/>
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

    // Matches the zero-size item limit of the other readers (AvroGeneratedCode.MaxZeroSizeItems).
    private const int MaxObjectsPerBlock = 1 << 16;

    private readonly byte[] _sync = new byte[AvroContainerFormat.SyncSize];
    private readonly PooledBufferWriter _block;
    private PooledBufferWriter? _compressed;
    private byte[]? _pendingHeader;
    private long _blockCount;
    private bool _disposed;

    // Sync's position: the stream's position when the writer was created (0 when it can't seek), and the bytes since.
    private readonly long _origin;
    private long _written;

    // The canonical form of Schema, computed when a block from another schema object is checked.
    private string? _canonicalForm;

    // appendSync: the sync marker of the file being appended to, whose header is already written; null for a new file.
    internal AvroFileWriter(Stream stream, AvroSchema schema, AvroWriteAction<T> write, AvroFileWriterOptions options, AvroCodec? codec = null, byte[]? appendSync = null)
    {
        _stream = stream;
        Schema = schema;
        _write = write;
        _codec = codec ?? options.Codec ?? AvroCodec.Null;
        _syncInterval = options.SyncInterval;
        _leaveOpen = options.LeaveOpen;
        _origin = stream.CanSeek ? stream.Position : 0;
        if (appendSync is null)
        {
            _pendingHeader = BuildHeader(options);
        }
        else
        {
            appendSync.CopyTo(_sync, 0);
        }

        // Sized for a whole block at the sync interval (up to 1 MiB), so a typical block needs no growth.
        _block = new PooledBufferWriter(PrefixRoom + Math.Min(_syncInterval, 1024 * 1024) + 1024);
        ClearBlock();
    }

    /// <summary>Gets the schema of the objects.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the codec that compresses each block.</summary>
    public AvroCodec Codec => _codec;

    /// <summary>Gets the file's sync marker, the 16 bytes that follow every block: random for a new file, the file's own when appending.</summary>
    public ReadOnlyMemory<byte> SyncMarker => _sync;

    /// <summary>
    /// Writes a block read from another file (<see cref="AvroFileReader{T}.TryReadBlock"/>) after the objects written so
    /// far, without decoding its objects. A block compressed with this file's codec is copied byte for byte, unless
    /// <paramref name="recompress"/> is set; any other block is decompressed with its codec and compressed with this
    /// file's.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="recompress">Whether to decompress and compress the block even when the codecs are the same.</param>
    /// <exception cref="ArgumentException">The block's schema is not this file's: their canonical forms differ.</exception>
    /// <exception cref="AvroDataException">
    /// The block cannot be decompressed, or decompresses to more than <see cref="AvroFileReaderOptions.DefaultMaxBlockLength"/>
    /// (64 MiB), a reader's default limit.
    /// </exception>
    public void WriteBlock(AvroFileBlock block, bool recompress = false)
    {
        var frame = FrameForeignBlock(block, recompress, AvroFileReaderOptions.DefaultMaxBlockLength);
        FlushBlock();
        WriteToStream(new ArraySegment<byte>(frame));
    }

    // WriteBlock with the limit of the reader the block came from (AvroFile.Concat).
    internal void WriteBlockWithLimit(AvroFileBlock block, bool recompress, int maxBlockLength)
    {
        var frame = FrameForeignBlock(block, recompress, maxBlockLength);
        FlushBlock();
        WriteToStream(new ArraySegment<byte>(frame));
    }

    /// <summary>Writes a block read from another file, as <see cref="WriteBlock"/> does, asynchronously.</summary>
    /// <param name="block">The block.</param>
    /// <param name="recompress">Whether to decompress and compress the block even when the codecs are the same.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="ArgumentException">The block's schema is not this file's: their canonical forms differ.</exception>
    /// <exception cref="AvroDataException">
    /// The block cannot be decompressed, or decompresses to more than <see cref="AvroFileReaderOptions.DefaultMaxBlockLength"/>.
    /// </exception>
    public async ValueTask WriteBlockAsync(AvroFileBlock block, bool recompress = false, CancellationToken cancellationToken = default)
    {
        var frame = FrameForeignBlock(block, recompress, AvroFileReaderOptions.DefaultMaxBlockLength);
        await FlushBlockAsync(cancellationToken).ConfigureAwait(false);
        await WriteToStreamAsync(new ArraySegment<byte>(frame), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the buffered objects as a block, if there are any, and returns the position after it: where the next block
    /// starts, which <see cref="AvroFileReader{T}.Seek"/> accepts, as Apache Avro Java's <c>DataFileWriter.sync()</c>
    /// returns. The position counts from where the stream was when the writer was created (from 0 when the stream can't
    /// seek). The stream itself is not flushed.
    /// </summary>
    public long Sync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushBlock();
        return _origin + _written;
    }

    /// <summary>Writes the buffered objects as a block, as <see cref="Sync"/> does, asynchronously, and returns the position after it.</summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async ValueTask<long> SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushBlockAsync(cancellationToken).ConfigureAwait(false);
        return _origin + _written;
    }

    /// <summary>Appends one object, writing a block when the buffered objects reach the sync interval.</summary>
    /// <param name="value">The object.</param>
    /// <remarks>If writing the object throws, nothing of it is kept and the writer stays usable.</remarks>
    public void Write(T value)
    {
        if (Append(value))
        {
            FlushBlock();
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
        Append(value) ? FlushBlockAsync(cancellationToken) : default;

    /// <summary>Writes the buffered objects as a block, if there are any, and flushes the stream.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushBlock();
        _stream.Flush();
    }

    /// <summary>Writes the buffered objects as a block, if there are any, and flushes the stream, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushBlockAsync(cancellationToken).ConfigureAwait(false);
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
            FlushBlock();
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
            await FlushBlockAsync(CancellationToken.None).ConfigureAwait(false);
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

        // The object limit keeps blocks of zero-size objects (nulls, empty records) readable by readers that bound them.
        _blockCount++;
        return _block.WrittenCount - PrefixRoom >= _syncInterval || _blockCount >= MaxObjectsPerBlock;
    }

    // The whole block as written: its count, size, data in this file's codec, and this file's sync marker.
    private byte[] FrameForeignBlock(AvroFileBlock block, bool recompress, int maxBlockLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(block);
        if (!ReferenceEquals(block.Schema, Schema)
            && !string.Equals(block.Schema.CanonicalForm, _canonicalForm ??= Schema.CanonicalForm, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The block's schema is not the file's: {block.Schema.CanonicalForm} is not {_canonicalForm}.", nameof(block));
        }

        using var recoded = recompress || !string.Equals(block.Codec.Name, _codec.Name, StringComparison.Ordinal)
            ? Recompress(block, maxBlockLength)
            : null;
        var data = recoded is null ? block.Data.Span : recoded.WrittenSpan;
        using var frame = new PooledBufferWriter(data.Length + PrefixRoom + AvroContainerFormat.SyncSize);
        var writer = new AvroWriter(frame);
        writer.WriteLong(block.Count);
        writer.WriteLong(data.Length);
        writer.WriteRaw(data);
        writer.WriteRaw(_sync);
        writer.Flush();
        return frame.ToArray();
    }

    // The block's objects, decompressed with its codec (at most a reader's default block limit) and compressed with
    // this file's codec.
    private PooledBufferWriter Recompress(AvroFileBlock block, int maxBlockLength)
    {
        // A guess at the decompressed size, never more than the limit: a hostile block can't reserve more.
        using var objects = new PooledBufferWriter((int)Math.Min(Math.Max(block.Data.Length * 4L, 256), maxBlockLength));
        try
        {
            block.Codec.Decompress(block.Data, new LimitedBufferWriter(objects, maxBlockLength));
        }
        catch (InvalidDataException ex)
        {
            throw new AvroDataException($"A block cannot be decompressed with the '{block.Codec.Name}' codec: {ex.Message}", ex);
        }

        var compressed = new PooledBufferWriter(objects.WrittenCount + 64);
        try
        {
            _codec.Compress(objects.WrittenMemory, compressed);
            return compressed;
        }
        catch
        {
            compressed.Dispose();
            throw;
        }
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
                writer.WriteBytes(entry.Value.Span);
            }
        }

        writer.WriteBlockEnd();
        writer.WriteRaw(_sync);
        writer.Flush();
        return header.ToArray();
    }

    private void FlushBlock()
    {
        if (_pendingHeader is { } header)
        {
            WriteToStream(new ArraySegment<byte>(header));
            _pendingHeader = null;
        }

        if (_blockCount == 0)
        {
            return;
        }

        var output = FrameBlock(out var block);
        try
        {
            WriteToStream(block);
        }
        finally
        {
            Unframe(output);
        }

        ClearBlock();
    }

    private async ValueTask FlushBlockAsync(CancellationToken cancellationToken)
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

    // Every write to the stream goes through these two, which count the bytes for Sync.
    private void WriteToStream(ArraySegment<byte> data)
    {
        _stream.Write(data.Array!, data.Offset, data.Count);
        _written += data.Count;
    }

    private async ValueTask WriteToStreamAsync(ArraySegment<byte> data, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        await _stream.WriteAsync(data.Array!, data.Offset, data.Count, cancellationToken).ConfigureAwait(false);
#else
        await _stream.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
#endif
        _written += data.Count;
    }

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
