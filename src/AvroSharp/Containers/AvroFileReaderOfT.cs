using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Containers;

/// <summary>
/// Reads an Avro object container file: the header when opened, then the objects block by block. Each block is
/// read whole, checked against the sync marker and decompressed before its objects are decoded. Reading is
/// synchronous (<see cref="TryRead"/>, <see cref="ReadAll"/>) or asynchronous (<see cref="ReadAllAsync"/>); the
/// asynchronous path does no synchronous I/O, and decodes each block synchronously once it is in memory.
/// </summary>
/// <typeparam name="T">The type of the objects.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed partial class AvroFileReader<T> : IDisposable, IAsyncDisposable
{
    private const int InputBufferSize = 16 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _maxBlockLength;
    private readonly long _maxZeroSizeValues;
    private readonly int _maxSchemaLength;

    // What an object that takes no bytes costs against MaxZeroSizeValuesPerBlock; 0 when every object takes a byte.
    private long _zeroSizeCost;
    private readonly IReadOnlyList<AvroCodec> _codecs;
    private readonly byte[] _sync = new byte[AvroContainerFormat.SyncSize];
    private AvroCodec _codec = AvroCodec.Null;
    private AvroReadFunc<T> _read = null!;

    // Buffered input from the stream: the unread bytes are _input[_inputStart.._inputEnd].
    private byte[] _input;
    private int _inputStart;
    private int _inputEnd;

    // The current block: its objects, decoded one at a time from _position.
    private byte[] _raw = [];
    private PooledBufferWriter? _decompressed;
    private ArraySegment<byte> _blockData;
    private LimitedBufferWriter? _limited;

    // Metadata entries found by the header parse in progress, as positions of their keys and values; null once parsed.
    private List<((int Start, int Length) Key, (int Start, int Length) Value)>? _headerEntries;
    private int _position;
    private long _objectsLeft;

    // Stream positions, when the stream can seek: the first block's start (just after the header), and the start of
    // the block most recently read (just after the previous sync marker).
    private long _firstBlockStart = -1;
    private long _blockStart = -1;

    // PastSync: the block start whose end-of-stream check is cached, and its result.
    private long _endCheckedFor = -1;
    private bool _blockIsAtEnd;
    private bool _disposed;

    internal AvroFileReader(Stream stream, AvroFileReaderOptions options)
    {
        _stream = stream;
        _leaveOpen = options.LeaveOpen;
        _maxBlockLength = options.MaxBlockLength;
        _maxZeroSizeValues = options.MaxZeroSizeValuesPerBlock;
        _maxSchemaLength = options.MaxSchemaLength;
        _codecs = options.Codecs;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>Gets the schema the objects were written with, from the header.</summary>
    public AvroSchema WriterSchema { get; private set; } = null!;

    /// <summary>Gets the header metadata, including the <c>avro.schema</c> and <c>avro.codec</c> entries.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Metadata { get; private set; } = null!;

    /// <summary>Gets the name of the codec the blocks are compressed with.</summary>
    public string Codec => _codec.Name;

    private int Buffered => _inputEnd - _inputStart;

    /// <summary>Gets a metadata entry decoded as UTF-8, or <see langword="null"/> when the header has no such key.</summary>
    /// <param name="key">The key.</param>
    public string? GetMetadataString(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Metadata.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value.Span) : null;
    }

    /// <summary>Reads the next object.</summary>
    /// <param name="value">The object, when one was read.</param>
    /// <returns><see langword="false"/> at the end of the file.</returns>
    /// <exception cref="AvroDataException">A block is malformed or truncated.</exception>
    public bool TryRead([MaybeNullWhen(false)] out T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_objectsLeft == 0)
        {
            if (!ReadBlock())
            {
                value = default;
                return false;
            }
        }

        value = DecodeNext();
        return true;
    }

    /// <summary>Reads the remaining objects.</summary>
    /// <exception cref="AvroDataException">A block is malformed or truncated.</exception>
    public IEnumerable<T> ReadAll()
    {
        while (TryRead(out var value))
        {
            yield return value;
        }
    }

    /// <summary>Reads the remaining objects, reading each block from the stream asynchronously.</summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <exception cref="AvroDataException">A block is malformed or truncated.</exception>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            while (_objectsLeft == 0)
            {
                if (!await ReadBlockAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }
            }

            yield return DecodeNext();
        }
    }

    /// <summary>Releases the buffers and, unless the options say otherwise, disposes the stream.</summary>
    public void Dispose()
    {
        if (ReleaseBuffers() && !_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Releases the buffers and, unless the options say otherwise, disposes the stream asynchronously.</summary>
    public async ValueTask DisposeAsync()
    {
        if (ReleaseBuffers() && !_leaveOpen)
        {
#if NETSTANDARD2_0
            _stream.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
#else
            await _stream.DisposeAsync().ConfigureAwait(false);
#endif
        }
    }

    internal void SetReader(AvroReadFunc<T> read) => _read = read;

    internal void ReadHeader()
    {
        while (!TryParseHeader(out var needed))
        {
            // The stream may end before the requested amount; only a fill that adds nothing means truncation.
            var before = Buffered;
            FillAtLeast(NextHeaderFill());
            if (Buffered == before)
            {
                throw HeaderTruncated(needed);
            }
        }

        MarkFirstBlock();
    }

    internal async ValueTask ReadHeaderAsync(CancellationToken cancellationToken)
    {
        while (!TryParseHeader(out var needed))
        {
            var before = Buffered;
            await FillAtLeastAsync(NextHeaderFill(), cancellationToken).ConfigureAwait(false);
            if (Buffered == before)
            {
                throw HeaderTruncated(needed);
            }
        }

        MarkFirstBlock();
    }

    private bool ReadBlock()
    {
        MarkBlockStart();
        long count;
        long size;
        while (!TryParseBlockPrefix(out count, out size))
        {
            if (!FillAtLeast(Buffered + 1))
            {
                if (Buffered == 0)
                {
                    return false;
                }

                throw Truncated("block header");
            }
        }

        var copied = StartBlock(count, size);
        if (!ReadDirect(_raw, copied, (int)size - copied))
        {
            throw Truncated("block");
        }

        if (!FillAtLeast(AvroContainerFormat.SyncSize))
        {
            throw Truncated("sync marker");
        }

        FinishBlock(count, (int)size);
        return true;
    }

    private async ValueTask<bool> ReadBlockAsync(CancellationToken cancellationToken)
    {
        var (read, count, size) = await ReadRawBlockAsync(cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return false;
        }

        FinishBlock(count, size);
        return true;
    }

    // Reads the next block's count, size and data (into _raw[0..size]) and buffers its sync marker, unchecked.
    private async ValueTask<(bool Read, long Count, int Size)> ReadRawBlockAsync(CancellationToken cancellationToken)
    {
        // Blocks already buffered are read without awaiting, so cancellation is checked here too.
        cancellationToken.ThrowIfCancellationRequested();
        MarkBlockStart();
        long count;
        long size;
        while (!TryParseBlockPrefix(out count, out size))
        {
            if (!await FillAtLeastAsync(Buffered + 1, cancellationToken).ConfigureAwait(false))
            {
                return Buffered == 0 ? default : throw Truncated("block header");
            }
        }

        var copied = StartBlock(count, size);
        if (!await ReadDirectAsync(_raw, copied, (int)size - copied, cancellationToken).ConfigureAwait(false))
        {
            throw Truncated("block");
        }

        if (!await FillAtLeastAsync(AvroContainerFormat.SyncSize, cancellationToken).ConfigureAwait(false))
        {
            throw Truncated("sync marker");
        }

        return (true, count, (int)size);
    }

    private T DecodeNext()
    {
        var reader = new AvroReader(_blockData.AsSpan(_position));
        var value = _read(ref reader);
        _position += (int)reader.BytesConsumed;
        if (--_objectsLeft == 0 && _position != _blockData.Count)
        {
            throw new AvroDataException($"A block has {_blockData.Count - _position} bytes left after its last object.");
        }

        return value;
    }

    private bool ReleaseBuffers()
    {
        if (_disposed)
        {
            return false;
        }

        _disposed = true;
        ReturnRaw();
        _decompressed?.Dispose();
        var input = _input;
        _input = [];
        ArrayPool<byte>.Shared.Return(input);
        return true;
    }

    private static AvroDataException Truncated(string what) => new($"The file ends inside a {what}.");
}
