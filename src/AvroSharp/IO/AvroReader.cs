using System;
using System.Buffers;
using System.Buffers.Binary;
#if NET8_0_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AvroSharp.IO;

/// <summary>
/// Reads Avro binary encoding from a <see cref="ReadOnlySpan{T}"/> or a (possibly multi-segment)
/// <see cref="ReadOnlySequence{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads from contiguous data never copy or allocate: <see cref="ReadBytesSpan"/> and <see cref="ReadFixedSpan"/> return
/// slices of the input. Only a value that straddles two segments of a sequence is copied.
/// </para>
/// <para>
/// Every length prefix is checked against the remaining input before anything is allocated, so malformed or
/// hostile input cannot trigger large allocations. Errors are reported as <see cref="AvroDataException"/>.
/// </para>
/// <para>This type is a <see langword="ref struct"/>: pass it by <see langword="ref"/> to methods that read values.</para>
/// </remarks>
public ref struct AvroReader
{
    private const int MaxVarint32Length = 5;
    private const int MaxVarint64Length = 10;

    private readonly ReadOnlySequence<byte> _sequence;
    private readonly long _length;
    private readonly bool _isMultiSegment;
    private ReadOnlySpan<byte> _span;
    private int _position;
    private long _consumedBefore;
    private SequencePosition _nextSegment;

    /// <summary>Initializes a reader over contiguous data.</summary>
    /// <param name="data">The Avro binary data.</param>
    public AvroReader(ReadOnlySpan<byte> data)
    {
        _sequence = default;
        _length = data.Length;
        _isMultiSegment = false;
        _span = data;
        _position = 0;
        _consumedBefore = 0;
        _nextSegment = default;
    }

    /// <summary>Initializes a reader over a sequence of buffers, such as the result of a <c>PipeReader</c> read.</summary>
    /// <param name="data">The Avro binary data.</param>
    public AvroReader(in ReadOnlySequence<byte> data)
    {
        _length = data.Length;
        _position = 0;
        _consumedBefore = 0;
        if (data.IsSingleSegment)
        {
            _sequence = default;
            _isMultiSegment = false;
            _span = data.First.Span;
            _nextSegment = default;
        }
        else
        {
            _sequence = data;
            _isMultiSegment = true;
            _span = default;
            _nextSegment = data.Start;
            MoveToNextSegment();
        }
    }

    /// <summary>Gets the number of bytes read so far.</summary>
    public readonly long BytesConsumed => _consumedBefore + _position;

    /// <summary>Gets the number of bytes left to read.</summary>
    public readonly long BytesRemaining => _length - BytesConsumed;

    /// <summary>Gets a value indicating whether all input has been read.</summary>
    public readonly bool IsAtEnd => BytesRemaining == 0;

    /// <summary>Reads <c>null</c>, which is encoded as zero bytes.</summary>
#pragma warning disable CA1822 // Part of the decoding API for symmetry with the other types.
    public readonly void ReadNull()
#pragma warning restore CA1822
    {
    }

    /// <summary>Reads a boolean encoded as one byte, 0 or 1.</summary>
    /// <exception cref="AvroDataException">The byte is neither 0 nor 1, or the input ended.</exception>
    public bool ReadBoolean() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new AvroDataException($"Invalid boolean byte 0x{b:X2} at offset {BytesConsumed - 1}; expected 0 or 1."),
    };

    /// <summary>Reads a zig-zag variable-length <c>int</c>.</summary>
    /// <exception cref="AvroDataException">The encoding is longer than 5 bytes, or the input ended.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt()
    {
        var value = ReadVarint32();
        return (int)(value >> 1) ^ -(int)(value & 1);
    }

    /// <summary>Reads a zig-zag variable-length <c>long</c>.</summary>
    /// <exception cref="AvroDataException">The encoding is longer than 10 bytes, or the input ended.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadLong()
    {
        var value = ReadVarint64();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    /// <summary>Reads a <c>float</c> from 4 little-endian bytes.</summary>
    public float ReadFloat()
    {
        if (_span.Length - _position >= sizeof(float))
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(_span[_position..]);
            _position += sizeof(float);
            return value;
        }

        Span<byte> bytes = stackalloc byte[sizeof(float)];
        ReadExactSlow(bytes);
        return BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    /// <summary>Reads a <c>double</c> from 8 little-endian bytes.</summary>
    public double ReadDouble()
    {
        if (_span.Length - _position >= sizeof(double))
        {
            var value = BinaryPrimitives.ReadDoubleLittleEndian(_span[_position..]);
            _position += sizeof(double);
            return value;
        }

        Span<byte> bytes = stackalloc byte[sizeof(double)];
        ReadExactSlow(bytes);
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    /// <summary>
    /// Reads <c>bytes</c> and returns them without copying when they are contiguous in the input
    /// (always, for span input). A value that straddles sequence segments is copied into a new array.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytesSpan() => ReadSpan(ReadLength("bytes"));

    /// <summary>Reads <c>bytes</c> into a new array.</summary>
    public byte[] ReadBytes() => ReadBytesSpan().ToArray();

    /// <summary>Reads a <c>string</c> and decodes it from UTF-8. Invalid UTF-8 is replaced with U+FFFD.</summary>
    public string ReadString()
    {
        var length = ReadLength("string");
        if (length == 0)
        {
            return string.Empty;
        }

        if (_span.Length - _position >= length)
        {
            var value = Encoding.UTF8.GetString(_span.Slice(_position, length));
            _position += length;
            return value;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var bytes = rented.AsSpan(0, length);
            ReadExactSlow(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Reads a <c>string</c> as UTF-8 bytes, without decoding and, when contiguous, without copying.</summary>
    public ReadOnlySpan<byte> ReadStringUtf8() => ReadSpan(ReadLength("string"));

    /// <summary>Reads a <c>fixed</c> value of <paramref name="size"/> bytes, without copying when contiguous.</summary>
    /// <param name="size">The schema's fixed size.</param>
    public ReadOnlySpan<byte> ReadFixedSpan(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        EnsureRemaining(size, "fixed");
        return ReadSpan(size);
    }

    /// <summary>Reads a <c>fixed</c> value into <paramref name="destination"/>, whose length is the schema's size.</summary>
    /// <param name="destination">Receives the bytes.</param>
    public void ReadFixed(scoped Span<byte> destination)
    {
        EnsureRemaining(destination.Length, "fixed");
        if (_span.Length - _position >= destination.Length)
        {
            _span.Slice(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
            return;
        }

        ReadExactSlow(destination);
    }

    /// <summary>Reads an enum symbol's zero-based ordinal.</summary>
    public int ReadEnum() => ReadInt();

    /// <summary>Reads the zero-based index of a union branch.</summary>
    public int ReadUnionIndex() => ReadInt();

    /// <summary>
    /// Reads the header of an array or map block: the number of items, or 0 at the end of the array or map.
    /// </summary>
    /// <param name="byteSize">
    /// The size in bytes of the block's items when the writer recorded it (a negative count on the wire), which
    /// allows the block to be skipped; otherwise -1.
    /// </param>
    /// <exception cref="AvroDataException">The count or size is invalid.</exception>
    public long ReadBlockCount(out long byteSize)
    {
        var count = ReadLong();
        if (count >= 0)
        {
            byteSize = -1;
            return count;
        }

        if (count == long.MinValue)
        {
            throw new AvroDataException($"Invalid block count {count}.");
        }

        byteSize = ReadLong();
        if (byteSize < 0 || byteSize > BytesRemaining)
        {
            throw new AvroDataException($"Invalid block size {byteSize}: {BytesRemaining} byte(s) remain.");
        }

        return -count;
    }

    /// <summary>
    /// Reads <c>double</c> array items into <paramref name="destination"/>. On little-endian hardware, contiguous
    /// input is a single copy.
    /// </summary>
    /// <param name="destination">Receives one value per element.</param>
    public void ReadDoubles(scoped Span<double> destination)
    {
        var byteCount = (long)destination.Length * sizeof(double);
        EnsureRemaining(byteCount, "double");
        if (BitConverter.IsLittleEndian && _span.Length - _position >= byteCount)
        {
            _span.Slice(_position, (int)byteCount).CopyTo(MemoryMarshal.AsBytes(destination));
            _position += (int)byteCount;
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadDouble();
        }
    }

    /// <summary>
    /// Reads <c>float</c> array items into <paramref name="destination"/>. On little-endian hardware, contiguous
    /// input is a single copy.
    /// </summary>
    /// <param name="destination">Receives one value per element.</param>
    public void ReadFloats(scoped Span<float> destination)
    {
        var byteCount = (long)destination.Length * sizeof(float);
        EnsureRemaining(byteCount, "float");
        if (BitConverter.IsLittleEndian && _span.Length - _position >= byteCount)
        {
            _span.Slice(_position, (int)byteCount).CopyTo(MemoryMarshal.AsBytes(destination));
            _position += (int)byteCount;
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ReadFloat();
        }
    }

    /// <summary>Skips a variable-length <c>int</c> or <c>long</c>.</summary>
    public void SkipVarint() => ReadVarint64();

    /// <summary>Skips a <c>bytes</c> or <c>string</c> value.</summary>
    public void SkipBytes() => Skip(ReadLength("bytes"));

    /// <summary>Skips <paramref name="count"/> bytes, for example a <c>fixed</c> value or a sized array block.</summary>
    /// <param name="count">The number of bytes to skip.</param>
    public void Skip(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureRemaining(count, "skipped");
        while (count > 0)
        {
            var available = _span.Length - _position;
            if (available == 0)
            {
                MoveToNextSegment();
                continue;
            }

            var step = (int)Math.Min(available, count);
            _position += step;
            count -= step;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadByte()
    {
        if (_position < _span.Length)
        {
            return _span[_position++];
        }

        return ReadByteSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte ReadByteSlow()
    {
        if (!MoveToNextSegment())
        {
            throw EndOfData();
        }

        return _span[_position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadVarint32()
    {
        // Inline fast paths for one- and two-byte values: small numbers, lengths, indexes and ordinals.
        var span = _span;
        var position = _position;
        if ((uint)position < (uint)span.Length)
        {
            uint first = span[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return first;
            }

            if ((uint)(position + 1) < (uint)span.Length)
            {
                uint second = span[position + 1];
                if (second < 0x80)
                {
                    _position = position + 2;
                    return (first & 0x7F) | (second << 7);
                }
            }
        }

        return ReadVarint32Multi();
    }

    /// <summary>
    /// Reads a three- or four-byte varint with unrolled byte reads, which beats the word path for short values.
    /// The inline path has already seen continuation bits on the first two bytes whenever two bytes were available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadThreeOrFourByteVarint(out uint value)
    {
        var span = _span;
        var position = _position;
        if (span.Length - position >= 4)
        {
            uint b0 = span[position];
            uint b1 = span[position + 1];
            uint b2 = span[position + 2];
            if (b2 < 0x80)
            {
                _position = position + 3;
                value = (b0 & 0x7F) | ((b1 & 0x7F) << 7) | (b2 << 14);
                return true;
            }

            uint b3 = span[position + 3];
            if (b3 < 0x80)
            {
                _position = position + 4;
                value = (b0 & 0x7F) | ((b1 & 0x7F) << 7) | ((b2 & 0x7F) << 14) | (b3 << 21);
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>A multi-byte <c>int</c>: decoded from one 8-byte read when enough input is contiguous.</summary>    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint ReadVarint32Multi()
    {
        if (TryReadThreeOrFourByteVarint(out var shortValue))
        {
            return shortValue;
        }

#if NET8_0_OR_GREATER
        if (_span.Length - _position >= sizeof(ulong))
        {
            var word = BinaryPrimitives.ReadUInt64LittleEndian(_span[_position..]);
            var stops = ~word & 0x8080808080808080UL;
            var length = (BitOperations.TrailingZeroCount(stops) >> 3) + 1;
            if (stops != 0 && length <= MaxVarint32Length)
            {
                _position += length;

                // Bits beyond 32 in a fifth byte are dropped, as in the byte-by-byte path (and Java).
                return (uint)CompactVarint(word, length);
            }
        }
#endif
        return ReadVarint32Slow();
    }

    /// <summary>A multi-byte <c>long</c>: decoded from one 8-byte read when it is at most 8 bytes long.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ReadVarint64Multi()
    {
        if (TryReadThreeOrFourByteVarint(out var shortValue))
        {
            return shortValue;
        }

#if NET8_0_OR_GREATER
        if (_span.Length - _position >= sizeof(ulong))
        {
            var word = BinaryPrimitives.ReadUInt64LittleEndian(_span[_position..]);
            var stops = ~word & 0x8080808080808080UL;
            if (stops != 0)
            {
                var length = (BitOperations.TrailingZeroCount(stops) >> 3) + 1;
                _position += length;
                return CompactVarint(word, length);
            }

            // No terminator in the first 8 bytes: a 9- or 10-byte value (full-range longs, negative values).
            // The first 56 bits come from the word; bits 56-62 and 63 from the next two bytes.
            if (_span.Length - _position >= MaxVarint64Length)
            {
                var result = CompactVarint(word, sizeof(ulong));
                var ninth = _span[_position + 8];
                result |= (ulong)(ninth & 0x7F) << 56;
                if (ninth < 0x80)
                {
                    _position += 9;
                    return result;
                }

                var tenth = _span[_position + 9];
                if (tenth < 0x80)
                {
                    // Only the lowest bit of a tenth byte fits in 64 bits; the rest is dropped, as in the byte loop.
                    _position += MaxVarint64Length;
                    return result | ((ulong)tenth << 63);
                }

                // An eleventh byte would follow: overlong. The byte loop reports it.
            }
        }
#endif
        return ReadVarint64Slow();
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Removes the continuation bits of the first <paramref name="length"/> (1 to 8) bytes of a little-endian word
    /// and packs their 7-bit groups together, without a loop or data-dependent branches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CompactVarint(ulong word, int length)
    {
        var keep = length == sizeof(ulong) ? ulong.MaxValue : (1UL << (length * 8)) - 1;
        var x = word & keep & 0x7F7F7F7F7F7F7F7FUL;
        x = ((x & 0x7F007F007F007F00UL) >> 1) | (x & 0x007F007F007F007FUL);
        x = ((x & 0x3FFF00003FFF0000UL) >> 2) | (x & 0x00003FFF00003FFFUL);
        x = ((x & 0x0FFFFFFF00000000UL) >> 4) | (x & 0x000000000FFFFFFFUL);
        return x;
    }
#endif

    private uint ReadVarint32Slow()
    {
        uint result = 0;
        for (var shift = 0; shift < 7 * MaxVarint32Length; shift += 7)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new AvroDataException($"Invalid int encoding at offset {BytesConsumed - MaxVarint32Length}: more than {MaxVarint32Length} bytes.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadVarint64()
    {
        // Inline fast paths for one- and two-byte values: small numbers, lengths, indexes and ordinals.
        var span = _span;
        var position = _position;
        if ((uint)position < (uint)span.Length)
        {
            ulong first = span[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return first;
            }

            if ((uint)(position + 1) < (uint)span.Length)
            {
                ulong second = span[position + 1];
                if (second < 0x80)
                {
                    _position = position + 2;
                    return (first & 0x7F) | (second << 7);
                }
            }
        }

        return ReadVarint64Multi();
    }

    private ulong ReadVarint64Slow()
    {
        ulong result = 0;
        for (var shift = 0; shift < 7 * MaxVarint64Length; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new AvroDataException($"Invalid long encoding at offset {BytesConsumed - MaxVarint64Length}: more than {MaxVarint64Length} bytes.");
    }

    private int ReadLength(string what)
    {
        var length = ReadLong();
        if (length < 0 || length > int.MaxValue)
        {
            throw new AvroDataException($"Invalid {what} length {length} at offset {BytesConsumed}.");
        }

        EnsureRemaining(length, what);
        return (int)length;
    }

    private readonly void EnsureRemaining(long count, string what)
    {
        if (count > BytesRemaining)
        {
            throw new AvroDataException($"Unexpected end of Avro data: {what} value needs {count} byte(s) at offset {BytesConsumed}, {BytesRemaining} remain.");
        }
    }

    private ReadOnlySpan<byte> ReadSpan(int length)
    {
        if (_span.Length - _position >= length)
        {
            var slice = _span.Slice(_position, length);
            _position += length;
            return slice;
        }

        var copy = new byte[length];
        ReadExactSlow(copy);
        return copy;
    }

    private void ReadExactSlow(scoped Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var available = _span.Length - _position;
            if (available == 0)
            {
                if (!MoveToNextSegment())
                {
                    throw EndOfData();
                }

                continue;
            }

            var step = Math.Min(available, destination.Length);
            _span.Slice(_position, step).CopyTo(destination);
            _position += step;
            destination = destination[step..];
        }
    }

    private bool MoveToNextSegment()
    {
        if (!_isMultiSegment)
        {
            return false;
        }

        while (_sequence.TryGet(ref _nextSegment, out var memory, advance: true))
        {
            _consumedBefore += _span.Length;
            _span = memory.Span;
            _position = 0;
            if (!_span.IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    private readonly AvroDataException EndOfData() =>
        new($"Unexpected end of Avro data at offset {BytesConsumed}.");
}
