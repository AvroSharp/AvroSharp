using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using AvroSharp.Buffers;
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
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(createReader);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream is not readable.", nameof(stream));
        }

        var reader = new AvroFileReader<T>(stream, options ?? AvroFileReaderOptions.Default);
        try
        {
            reader.SetReader(createReader(reader.WriterSchema) ?? throw new InvalidOperationException("createReader returned null."));
            return reader;
        }
        catch
        {
            reader.Dispose();
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
        Open<AvroValue>(
            stream,
            writerSchema =>
            {
                var datumReader = readerSchema is null
                    ? GenericDatumReader.Create(writerSchema, readerOptions)
                    : GenericDatumReader.Create(writerSchema, readerSchema, readerOptions);
                return (ref AvroReader reader) => datumReader.Read(ref reader);
            },
            options);
}

/// <summary>
/// Reads an Avro object container file: the header when opened, then the objects block by block. Each block is
/// read whole, decompressed and checked against the sync marker before its objects are decoded.
/// </summary>
/// <typeparam name="T">The type of the objects.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class AvroFileReader<T> : IDisposable
{
    private const int InputBufferSize = 16 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _maxBlockLength;
    private readonly AvroCodec _codec;
    private readonly byte[] _sync = new byte[AvroContainerFormat.SyncSize];
    private AvroReadFunc<T> _read = null!;

    // Buffered input from the stream.
    private byte[] _input;
    private int _inputStart;
    private int _inputEnd;

    // The current block: its objects, decoded one at a time from _position to _blockLength.
    private byte[] _raw = [];
    private PooledBufferWriter? _decompressed;
    private ReadOnlyMemory<byte> _blockData;
    private int _position;
    private long _objectsLeft;
    private bool _disposed;

    internal AvroFileReader(Stream stream, AvroFileReaderOptions options)
    {
        _stream = stream;
        _leaveOpen = options.LeaveOpen;
        _maxBlockLength = options.MaxBlockLength;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
        try
        {
            (WriterSchema, Metadata) = ReadHeader();
            var codecName = GetMetadataString(AvroContainerFormat.CodecKey) ?? AvroCodecNames.Null;
            _codec = FindCodec(codecName, options.Codecs)
                ?? throw new AvroException($"The file is compressed with the '{codecName}' codec, which is not available; add it to AvroFileReaderOptions.Codecs.");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(_input);
            throw;
        }
    }

    /// <summary>Gets the schema the objects were written with, from the header.</summary>
    public AvroSchema WriterSchema { get; }

    /// <summary>Gets the header metadata, including the <c>avro.schema</c> and <c>avro.codec</c> entries.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Metadata { get; }

    /// <summary>Gets the name of the codec the blocks are compressed with.</summary>
    public string Codec => _codec.Name;

    internal void SetReader(AvroReadFunc<T> read) => _read = read;

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
                value = default!;
                return false;
            }
        }

        var reader = new AvroReader(_blockData.Span[_position..]);
        value = _read(ref reader);
        _position += (int)reader.BytesConsumed;
        if (--_objectsLeft == 0 && _position != _blockData.Length)
        {
            throw new AvroDataException($"A block has {_blockData.Length - _position} bytes left after its last object.");
        }

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

    /// <summary>Releases the buffers and, unless the options say otherwise, disposes the stream.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReturnRaw();
        _decompressed?.Dispose();
        var input = _input;
        _input = [];
        ArrayPool<byte>.Shared.Return(input);
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static AvroCodec? FindCodec(string name, IReadOnlyList<AvroCodec> extra)
    {
        foreach (var codec in extra)
        {
            if (codec is not null && string.Equals(codec.Name, name, StringComparison.Ordinal))
            {
                return codec;
            }
        }

        return name switch
        {
            AvroCodecNames.Null => AvroCodec.Null,
            AvroCodecNames.Deflate => AvroCodec.Deflate,
            _ => null,
        };
    }

    private (AvroSchema Schema, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Metadata) ReadHeader()
    {
        Span<byte> magic = stackalloc byte[4];
        if (!TryReadExactly(magic) || !magic.SequenceEqual(AvroContainerFormat.Magic))
        {
            throw new AvroDataException("The data is not an Avro object container file: it does not start with 'Obj' and version 1.");
        }

        var metadata = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        while (true)
        {
            var count = ReadLong("metadata block count");
            if (count == 0)
            {
                break;
            }

            if (count < 0)
            {
                count = -count;
                ReadLong("metadata block size");
            }

            for (var i = 0L; i < count; i++)
            {
                var key = Encoding.UTF8.GetString(ReadLengthPrefixed("metadata key"));
                metadata[key] = ReadLengthPrefixed("metadata value");
            }
        }

        ReadExactly(_sync, "sync marker");

        if (!metadata.TryGetValue(AvroContainerFormat.SchemaKey, out var schemaJson))
        {
            throw new AvroDataException("The file header has no 'avro.schema' entry.");
        }

        AvroSchema schema;
        try
        {
            schema = AvroSchema.Parse(schemaJson.Span);
        }
        catch (AvroSchemaException ex)
        {
            throw new AvroDataException($"The file's schema is invalid: {ex.Message}", ex);
        }

        return (schema, metadata);
    }

    private bool ReadBlock()
    {
        if (!FillAtLeast(1))
        {
            return false;
        }

        var count = ReadLong("block count");
        var size = ReadLong("block size");
        if (count < 0 || size < 0)
        {
            throw new AvroDataException($"A block declares {count} objects in {size} bytes.");
        }

        if (size > _maxBlockLength)
        {
            throw new AvroDataException($"A block of {size} bytes is larger than the limit of {_maxBlockLength} bytes (AvroFileReaderOptions.MaxBlockLength).");
        }

        ReturnRaw();
        _raw = ArrayPool<byte>.Shared.Rent(Math.Max((int)size, 1));
        ReadExactly(_raw.AsSpan(0, (int)size), "block");

        Span<byte> sync = stackalloc byte[AvroContainerFormat.SyncSize];
        ReadExactly(sync, "sync marker");
        if (!sync.SequenceEqual(_sync))
        {
            throw new AvroDataException("A block is not followed by the file's sync marker; the file is corrupt.");
        }

        _blockData = Decompress(_raw.AsMemory(0, (int)size));

        // Every object takes at least one byte except zero-size ones (null, empty records), which input cannot bound.
        if (count > _blockData.Length && count > AvroGeneratedCode.MaxZeroSizeItems)
        {
            throw new AvroDataException($"A block declares {count} objects in {_blockData.Length} bytes.");
        }

        if (count == 0 && _blockData.Length != 0)
        {
            throw new AvroDataException($"A block declares no objects but holds {_blockData.Length} bytes.");
        }

        _position = 0;
        _objectsLeft = count;
        return true;
    }

    private ReadOnlyMemory<byte> Decompress(ReadOnlyMemory<byte> raw)
    {
        if (ReferenceEquals(_codec, AvroCodec.Null))
        {
            return raw;
        }

        _decompressed ??= new PooledBufferWriter(Math.Max(raw.Length * 4, 256));
        _decompressed.Clear();
        try
        {
            _codec.Decompress(raw, new LimitedBufferWriter(_decompressed, _maxBlockLength));
        }
        catch (InvalidDataException ex)
        {
            throw new AvroDataException($"A block cannot be decompressed with the '{_codec.Name}' codec: {ex.Message}", ex);
        }

        return _decompressed.WrittenMemory;
    }

    private void ReturnRaw()
    {
        if (_raw.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_raw);
            _raw = [];
        }
    }

    private long ReadLong(string what)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_inputStart == _inputEnd && !FillAtLeast(1))
            {
                throw Truncated(what);
            }

            var b = _input[_inputStart++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return (long)(result >> 1) ^ -(long)(result & 1);
            }
        }

        throw new AvroDataException($"The {what} is a varint longer than 10 bytes.");
    }

    private byte[] ReadLengthPrefixed(string what)
    {
        var length = ReadLong(what + " length");
        if (length < 0 || length > _maxBlockLength)
        {
            throw new AvroDataException($"A {what} declares {length} bytes.");
        }

        var bytes = new byte[length];
        ReadExactly(bytes, what);
        return bytes;
    }

    private void ReadExactly(Span<byte> destination, string what)
    {
        if (!TryReadExactly(destination))
        {
            throw Truncated(what);
        }
    }

    private bool TryReadExactly(Span<byte> destination)
    {
        var buffered = Math.Min(destination.Length, _inputEnd - _inputStart);
        _input.AsSpan(_inputStart, buffered).CopyTo(destination);
        _inputStart += buffered;
        destination = destination[buffered..];
        if (destination.IsEmpty)
        {
            return true;
        }

        // Large reads go straight to the destination; small ones refill the buffer.
        if (destination.Length >= _input.Length)
        {
            while (!destination.IsEmpty)
            {
                var read = _stream.Read(destination);
                if (read == 0)
                {
                    return false;
                }

                destination = destination[read..];
            }

            return true;
        }

        if (!FillAtLeast(destination.Length))
        {
            return false;
        }

        _input.AsSpan(_inputStart, destination.Length).CopyTo(destination);
        _inputStart += destination.Length;
        return true;
    }

    // Makes at least `count` bytes (no more than the buffer size) available in _input; false at the end of the stream.
    private bool FillAtLeast(int count)
    {
        if (_inputEnd - _inputStart >= count)
        {
            return true;
        }

        if (_inputStart > 0)
        {
            _input.AsSpan(_inputStart, _inputEnd - _inputStart).CopyTo(_input);
            _inputEnd -= _inputStart;
            _inputStart = 0;
        }

        while (_inputEnd < count)
        {
            var read = _stream.Read(_input, _inputEnd, _input.Length - _inputEnd);
            if (read == 0)
            {
                return false;
            }

            _inputEnd += read;
        }

        return true;
    }

    private static AvroDataException Truncated(string what) => new($"The file ends inside a {what}.");
}
