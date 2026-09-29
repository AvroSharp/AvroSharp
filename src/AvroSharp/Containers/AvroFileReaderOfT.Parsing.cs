using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Containers;

/// <summary>
/// Parsing and buffering. The header and block prefixes are parsed from the buffered input; when it ends too soon,
/// the caller fills more (synchronously or asynchronously) and parsing starts again, so both paths share one parser.
/// </summary>
public sealed partial class AvroFileReader<T>
{
    private const string MagicPart = "magic";

    private bool TryParseHeader(out string needed)
    {
        var cursor = new Cursor(_input.AsSpan(_inputStart, Buffered));
        needed = MagicPart;
        if (!cursor.TrySlice(AvroContainerFormat.Magic.Length, out var magic))
        {
            return false;
        }

        if (!magic.SequenceEqual(AvroContainerFormat.Magic))
        {
            throw NotAContainer();
        }

        // Entries are recorded as positions in the buffer, and turned into strings and copies only once the whole
        // header is buffered: an attempt that runs out of data costs no allocations.
        var entries = _headerEntries ??= [];
        entries.Clear();
        if (!TryParseMetadataEntries(ref cursor, entries, out needed))
        {
            return false;
        }

        needed = "sync marker";
        if (!cursor.TrySlice(AvroContainerFormat.SyncSize, out var sync))
        {
            return false;
        }

        var metadata = BuildMetadata(_input.AsSpan(_inputStart, cursor.Position), entries);
        _headerEntries = null;
        sync.CopyTo(_sync);
        _inputStart += cursor.Position;
        SetHeader(metadata);
        return true;
    }

    // The metadata map: blocks of key/value pairs, ended by a zero count.
    private const int MaxMetadataEntries = 1024;

    private bool TryParseMetadataEntries(ref Cursor cursor, List<((int Start, int Length) Key, (int Start, int Length) Value)> entries, out string needed)
    {
        while (true)
        {
            needed = "metadata block count";
            if (!cursor.TryReadLong(needed, out var count))
            {
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            if (count < 0)
            {
                count = -count;
                needed = "metadata block size";
                if (!cursor.TryReadLong(needed, out _))
                {
                    return false;
                }
            }

            // Files hold a handful of entries; without a bound, a header of empty pairs costs 16 bytes per 2 (#129).
            if (count > MaxMetadataEntries - entries.Count)
            {
                throw new AvroDataException($"The file header declares more than {MaxMetadataEntries} metadata entries.");
            }

            for (var i = 0L; i < count; i++)
            {
                if (!TryReadLengthPrefixed(ref cursor, "metadata key", "metadata key length", out var key, out needed)
                    || !TryReadLengthPrefixed(ref cursor, "metadata value", "metadata value length", out var value, out needed))
                {
                    return false;
                }

                entries.Add((key, value));
            }
        }
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> BuildMetadata(
        ReadOnlySpan<byte> header, List<((int Start, int Length) Key, (int Start, int Length) Value)> entries)
    {
        var metadata = new Dictionary<string, ReadOnlyMemory<byte>>(entries.Count, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            metadata[Encoding.UTF8.GetString(header.Slice(key.Start, key.Length))] = header.Slice(value.Start, value.Length).ToArray();
        }

        return metadata;
    }

    // Reads a length and skips that many bytes; `range` is where they are, relative to the cursor's start. The names
    // are constants, so a retried parse allocates nothing for them.
    private bool TryReadLengthPrefixed(ref Cursor cursor, string what, string whatLength, out (int Start, int Length) range, out string needed)
    {
        range = default;
        needed = whatLength;
        if (!cursor.TryReadLong(needed, out var length))
        {
            return false;
        }

        if (length < 0 || length > _maxBlockLength)
        {
            throw new AvroDataException($"A {what} declares {length} bytes.");
        }

        needed = what;
        var start = cursor.Position;
        if (!cursor.TrySlice((int)length, out _))
        {
            return false;
        }

        range = (start, (int)length);
        return true;
    }

    private void SetHeader(Dictionary<string, ReadOnlyMemory<byte>> metadata)
    {
        if (!metadata.TryGetValue(AvroContainerFormat.SchemaKey, out var schemaJson))
        {
            throw new AvroDataException("The file header has no 'avro.schema' entry.");
        }

        if (schemaJson.Length > _maxSchemaLength)
        {
            throw new AvroDataException($"The file's schema is {schemaJson.Length} bytes, more than the limit of {_maxSchemaLength} (AvroFileReaderOptions.MaxSchemaLength).");
        }

        try
        {
            WriterSchema = AvroSchema.Parse(schemaJson.Span);
            _zeroSizeCost = ZeroSizeValues.Count(WriterSchema);
        }
        catch (AvroSchemaException ex)
        {
            throw new AvroDataException($"The file's schema is invalid: {ex.Message}", ex);
        }

        Metadata = metadata;
        var codecName = GetMetadataString(AvroContainerFormat.CodecKey) ?? AvroCodecNames.Null;
        _codec = FindCodec(codecName, _codecs) ?? throw CodecNotAvailable(codecName);
    }

    private static AvroException CodecNotAvailable(string name) => AvroCodecNames.IsStandard(name)
        ? new($"The file is compressed with the '{name}' codec, which is not available. Reference the AvroSharp.Codecs package and set AvroFileReaderOptions.Codecs to AvroCodecs.All.")
        : new($"The file is compressed with the '{name}' codec, which is not available; add it to AvroFileReaderOptions.Codecs.");

    // The header is parsed again after each fill, so the buffered amount grows geometrically, up to the limit.
    private int NextHeaderFill()
    {
        if (Buffered >= _maxBlockLength)
        {
            throw new AvroDataException($"The file header is larger than the limit of {_maxBlockLength} bytes (AvroFileReaderOptions.MaxBlockLength).");
        }

        return (int)Math.Min((long)Buffered + Math.Max(Buffered, 256), _maxBlockLength);
    }

    private static AvroDataException HeaderTruncated(string needed) =>
        ReferenceEquals(needed, MagicPart) ? NotAContainer() : Truncated(needed);

    private static AvroDataException NotAContainer() =>
        new("The data is not an Avro object container file: it does not start with 'Obj' and version 1.");

    private bool TryParseBlockPrefix(out long count, out long size)
    {
        var cursor = new Cursor(_input.AsSpan(_inputStart, Buffered));
        size = 0;
        if (!cursor.TryReadLong("block count", out count) || !cursor.TryReadLong("block size", out size))
        {
            return false;
        }

        _inputStart += cursor.Position;
        return true;
    }

    // Checks the block's prefix, rents its buffer and copies what is already buffered; returns the bytes copied.
    private int StartBlock(long count, long size)
    {
        if (count < 0 || size < 0)
        {
            throw new AvroDataException($"A block declares {count} objects in {size} bytes.");
        }

        if (size > _maxBlockLength)
        {
            throw new AvroDataException($"A block of {size} bytes is larger than the limit of {_maxBlockLength} bytes (AvroFileReaderOptions.MaxBlockLength).");
        }

        // Kept from block to block while it is large enough; blocks near the sync interval rarely need a new one.
        if (_raw.Length < size || _raw.Length == 0)
        {
            ReturnRaw();
            _raw = ArrayPool<byte>.Shared.Rent(Math.Max((int)size, 1));
        }

        var copied = Math.Min(Buffered, (int)size);
        _input.AsSpan(_inputStart, copied).CopyTo(_raw);
        _inputStart += copied;
        return copied;
    }

    // Checks the sync marker (buffered by the caller) and prepares the block's objects for decoding.
    private void FinishBlock(long count, int size)
    {
        ConsumeSyncMarker();
        _blockData = Decompress(new ArraySegment<byte>(_raw, 0, size));
        CheckObjectCount(count, _blockData.Count);
        _position = 0;
        _objectsLeft = count;
    }

    // Checks and skips the sync marker after a block (buffered by the caller).
    private void ConsumeSyncMarker()
    {
        if (!_input.AsSpan(_inputStart, AvroContainerFormat.SyncSize).SequenceEqual(_sync))
        {
            throw new AvroDataException("A block is not followed by the file's sync marker; the file is corrupt.");
        }

        _inputStart += AvroContainerFormat.SyncSize;
    }

    private void CheckObjectCount(long count, int length)
    {
        // Every object takes at least one byte unless the schema allows zero-size ones, which only the option bounds.
        if (count > length && (_zeroSizeCost == 0 || count > _maxZeroSizeValues / _zeroSizeCost))
        {
            var limit = _zeroSizeCost == 0 ? string.Empty : " (AvroFileReaderOptions.MaxZeroSizeValuesPerBlock)";
            throw new AvroDataException($"A block declares {count} objects in {length} bytes{limit}.");
        }

        if (count == 0 && length != 0)
        {
            throw new AvroDataException($"A block declares no objects but holds {length} bytes.");
        }
    }

    private ArraySegment<byte> Decompress(ArraySegment<byte> raw)
    {
        if (ReferenceEquals(_codec, AvroCodec.Null))
        {
            return raw;
        }

        _decompressed ??= new PooledBufferWriter(Math.Max(raw.Count * 4, 256));
        _limited ??= new LimitedBufferWriter(_decompressed, _maxBlockLength);
        _decompressed.Clear();
        DecompressInto(raw, _limited);
        return _decompressed.WrittenSegment;
    }

    private void DecompressInto(ArraySegment<byte> raw, LimitedBufferWriter destination)
    {
        try
        {
            _codec.Decompress(raw, destination);
        }
        catch (InvalidDataException ex)
        {
            throw new AvroDataException($"A block cannot be decompressed with the '{_codec.Name}' codec: {ex.Message}", ex);
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

    private void ReturnRaw()
    {
        if (_raw.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_raw);
            _raw = [];
        }
    }

    // Reads until at least `count` bytes are buffered; false if the stream ends first.
    private bool FillAtLeast(int count)
    {
        PrepareFill(count);
        while (_inputEnd - _inputStart < count)
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

    private async ValueTask<bool> FillAtLeastAsync(int count, CancellationToken cancellationToken)
    {
        PrepareFill(count);
        while (_inputEnd - _inputStart < count)
        {
#if NETSTANDARD2_0
            var read = await _stream.ReadAsync(_input, _inputEnd, _input.Length - _inputEnd, cancellationToken).ConfigureAwait(false);
#else
            var read = await _stream.ReadAsync(_input.AsMemory(_inputEnd), cancellationToken).ConfigureAwait(false);
#endif
            if (read == 0)
            {
                return false;
            }

            _inputEnd += read;
        }

        return true;
    }

    // Moves the unread bytes to the start of the buffer, growing it when `count` bytes would not fit.
    private void PrepareFill(int count)
    {
        if (Buffered >= count)
        {
            return;
        }

        var target = _input;
        if (count > _input.Length)
        {
            target = ArrayPool<byte>.Shared.Rent(count);
        }

        _input.AsSpan(_inputStart, Buffered).CopyTo(target);
        _inputEnd = Buffered;
        _inputStart = 0;
        if (!ReferenceEquals(target, _input))
        {
            ArrayPool<byte>.Shared.Return(_input);
            _input = target;
        }
    }

    private bool ReadDirect(byte[] destination, int offset, int count)
    {
        while (count > 0)
        {
            var read = _stream.Read(destination, offset, count);
            if (read == 0)
            {
                return false;
            }

            offset += read;
            count -= read;
        }

        return true;
    }

    private async ValueTask<bool> ReadDirectAsync(byte[] destination, int offset, int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
#if NETSTANDARD2_0
            var read = await _stream.ReadAsync(destination, offset, count, cancellationToken).ConfigureAwait(false);
#else
            var read = await _stream.ReadAsync(destination.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
#endif
            if (read == 0)
            {
                return false;
            }

            offset += read;
            count -= read;
        }

        return true;
    }

    /// <summary>Reads varints and slices from buffered bytes; reports incomplete data instead of throwing.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public bool TryReadLong(string what, out long value)
        {
            ulong result = 0;
            var position = Position;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position == _data.Length)
                {
                    value = 0;
                    return false;
                }

                var b = _data[position++];
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    Position = position;
                    value = (long)(result >> 1) ^ -(long)(result & 1);
                    return true;
                }
            }

            throw new AvroDataException($"The {what} is a varint longer than 10 bytes.");
        }

        public bool TrySlice(int length, out ReadOnlySpan<byte> slice)
        {
            if (_data.Length - Position < length)
            {
                slice = default;
                return false;
            }

            slice = _data.Slice(Position, length);
            Position += length;
            return true;
        }
    }
}
