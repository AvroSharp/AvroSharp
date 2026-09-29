using System;
using System.Buffers;
using System.Buffers.Binary;
#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AvroSharp.IO;

/// <summary>
/// Writes Avro binary encoding directly into an <see cref="IBufferWriter{T}"/> or a fixed <see cref="Span{T}"/>,
/// without intermediate buffers or per-value allocations.
/// </summary>
/// <remarks>
/// <para>
/// When writing to an <see cref="IBufferWriter{T}"/>, bytes are committed to it in chunks; call <see cref="Flush"/>
/// once finished to commit the rest. When writing to a span, <see cref="BytesWritten"/> gives the length used.
/// </para>
/// <para>
/// This type is a <see langword="ref struct"/>: pass it by <see langword="ref"/> to methods that write values.
/// It writes values only; it does not check them against a schema.
/// </para>
/// </remarks>
public ref struct AvroWriter
{
    private const int MinimumBufferSize = 256;
    private const int MaxVarint64Length = 10;

    private IBufferWriter<byte>? _output;
    private Span<byte> _buffer;
    private int _buffered;
    private long _committed;

    // A span writer created for a Try* write: running out of room switches to a discarding output instead of throwing,
    // and Overflowed reports it afterwards.
    private readonly bool _discardOverflow;
    private bool _overflowed;

    /// <summary>Initializes a writer that appends to <paramref name="output"/>.</summary>
    /// <param name="output">The destination; call <see cref="Flush"/> to commit the final bytes to it.</param>
    public AvroWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
#if NET8_0_OR_GREATER
        FastBmi2.EnsureInitialized();
#endif
        _output = output;
        _buffer = default;
        _buffered = 0;
        _committed = 0;
    }

    /// <summary>Initializes a writer over a fixed destination. Writing more than it holds throws <see cref="AvroException"/>.</summary>
    /// <param name="destination">The buffer to write into.</param>
    public AvroWriter(Span<byte> destination)
        : this(destination, discardOverflow: false)
    {
    }

    /// <summary>Initializes a writer over a fixed destination that, when <paramref name="discardOverflow"/> is set, records overflow instead of throwing.</summary>
    internal AvroWriter(Span<byte> destination, bool discardOverflow)
    {
#if NET8_0_OR_GREATER
        FastBmi2.EnsureInitialized();
#endif
        _output = null;
        _buffer = destination;
        _buffered = 0;
        _committed = 0;
        _discardOverflow = discardOverflow;
        _overflowed = false;
    }

    /// <summary>Gets a value indicating whether a writer created to discard overflow ran out of room; its output is then incomplete.</summary>
    internal readonly bool Overflowed => _overflowed;

    /// <summary>Gets the total number of bytes written, including bytes not yet flushed.</summary>
    public readonly long BytesWritten => _committed + _buffered;

    /// <summary>Gets the number of bytes written but not yet committed to the <see cref="IBufferWriter{T}"/>.</summary>
    public readonly int BytesPending => _output is null ? 0 : _buffered;

    /// <summary>Commits pending bytes to the <see cref="IBufferWriter{T}"/>. Does nothing for a span destination.</summary>
    public void Flush()
    {
        if (_output is null || _buffered == 0)
        {
            return;
        }

        _output.Advance(_buffered);
        _committed += _buffered;
        _buffered = 0;
        _buffer = default;
    }

    /// <summary>Writes <c>null</c>, which is encoded as zero bytes.</summary>
#pragma warning disable CA1822 // Part of the encoding API for symmetry with the other types.
    public readonly void WriteNull()
#pragma warning restore CA1822
    {
    }

    /// <summary>Writes a boolean as one byte, 0 or 1.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value)
    {
        Ensure(1);
        At(_buffered++) = value ? (byte)1 : (byte)0;
    }

    /// <summary>Writes an <c>int</c> as a zig-zag variable-length integer (1 to 5 bytes).</summary>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt(int value) => WriteVarint32((uint)((value << 1) ^ (value >> 31)));

    /// <summary>Writes a <c>long</c> as a zig-zag variable-length integer (1 to 10 bytes).</summary>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLong(long value) => WriteVarint64((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Writes a <c>float</c> as 4 little-endian bytes of its IEEE 754 bit pattern.</summary>
    /// <param name="value">The value.</param>
    public void WriteFloat(float value)
    {
        Ensure(sizeof(float));
        if (BitConverter.IsLittleEndian)
        {
            Unsafe.WriteUnaligned(ref At(_buffered), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer[_buffered..], value);
        }

        _buffered += sizeof(float);
    }

    /// <summary>Writes a <c>double</c> as 8 little-endian bytes of its IEEE 754 bit pattern.</summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value)
    {
        Ensure(sizeof(double));
        if (BitConverter.IsLittleEndian)
        {
            Unsafe.WriteUnaligned(ref At(_buffered), value);
        }
        else
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_buffered..], value);
        }

        _buffered += sizeof(double);
    }

    /// <summary>Writes <c>bytes</c>: the length as a <c>long</c>, then the bytes.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBytes(scoped ReadOnlySpan<byte> value)
    {
        WriteLong(value.Length);
        WriteRaw(value);
    }

    /// <summary>Writes a <c>string</c>: the UTF-8 length as a <c>long</c>, then the UTF-8 bytes, encoded in place.</summary>
    /// <param name="value">The string.</param>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteString(value.AsSpan());
    }

    /// <summary>Writes a <c>string</c>: the UTF-8 length as a <c>long</c>, then the UTF-8 bytes, encoded in place.</summary>
    /// <param name="value">The characters.</param>
    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            WriteVarint32(0);
            return;
        }

        // One pass when the buffer has room for the longest possible encoding (3 bytes per UTF-16 char) after room
        // for its length prefix: encode, then write the real length and, if its prefix is shorter, move the bytes
        // down. Otherwise count first, so the prefix can be written before the bytes; both passes are vectorized.
        var maxBytes = (long)value.Length * 3;
        var maxPrefix = VarintLength((ulong)maxBytes << 1);
        if (maxPrefix + maxBytes <= _buffer.Length - _buffered)
        {
            var start = _buffered;
            var written = Encoding.UTF8.GetBytes(value, _buffer[(start + maxPrefix)..]);
            var length = (ulong)written << 1;
            var prefix = VarintLength(length);
            if (prefix < maxPrefix)
            {
                _buffer.Slice(start + maxPrefix, written).CopyTo(_buffer[(start + prefix)..]);
            }

            _ = WriteVarintLoop(_buffer[start..], length);
            _buffered = start + prefix + written;
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteLong(byteCount);
        Ensure(byteCount);
        _buffered += Encoding.UTF8.GetBytes(value, _buffer[_buffered..]);
    }

    /// <summary>Writes a <c>string</c> that is already UTF-8 encoded.</summary>
    /// <param name="utf8">The UTF-8 bytes; they are not validated.</param>
    public void WriteStringUtf8(scoped ReadOnlySpan<byte> utf8) => WriteBytes(utf8);

    /// <summary>Writes a <c>fixed</c> value: exactly its bytes, with no length prefix.</summary>
    /// <param name="value">The bytes; the caller ensures the length matches the schema's size.</param>
    public void WriteFixed(scoped ReadOnlySpan<byte> value) => WriteRaw(value);

    /// <summary>Writes an enum symbol's zero-based ordinal as an <c>int</c>.</summary>
    /// <param name="ordinal">The ordinal.</param>
    public void WriteEnum(int ordinal) => WriteInt(ordinal);

    /// <summary>Writes the zero-based index of a union branch as an <c>int</c>.</summary>
    /// <param name="index">The branch index.</param>
    public void WriteUnionIndex(int index) => WriteInt(index);

    /// <summary>
    /// Writes the item count that starts an array or map block. Follow it with that many items (or key/value pairs),
    /// then more blocks, then <see cref="WriteBlockEnd"/>.
    /// </summary>
    /// <param name="count">The number of items in the block; must be positive.</param>
    public void WriteBlockCount(long count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A block count must be positive; use WriteBlockEnd to end an array or map.");
        }

        WriteLong(count);
    }

    /// <summary>Writes the zero count that ends an array or map.</summary>
    public void WriteBlockEnd() => WriteVarint32(0);

    /// <summary>
    /// Writes <c>boolean</c> array items as a single copy, since each item is one byte in memory and on the wire.
    /// </summary>
    /// <param name="values">The items; write the block count first.</param>
    public void WriteBooleans(scoped ReadOnlySpan<bool> values)
    {
        var bytes = MemoryMarshal.AsBytes(values);
        if (AvroReader.IndexOfInvalidBoolean(bytes) < 0)
        {
            WriteRaw(bytes);
            return;
        }

        // A bool made by unsafe code can hold other bytes; write those as true, as WriteBoolean does.
        foreach (var value in values)
        {
            WriteBoolean(value);
        }
    }

    /// <summary>Writes <c>int</c> array items, each as a zig-zag varint.</summary>
    /// <param name="values">The items; write the block count first.</param>
    /// <remarks>See <see cref="WriteLongs"/>.</remarks>
    public void WriteInts(scoped ReadOnlySpan<int> values)
    {
        var i = 0;
        while (i < values.Length)
        {
            var room = (_buffer.Length - _buffered) / MaxVarint64Length;
            if (room == 0)
            {
                // Grows the buffer, or near the end of a fixed span writes only the bytes the value needs.
                WriteInt(values[i++]);
                continue;
            }

            var end = Math.Min(values.Length, i + room);
            ref var buffer = ref MemoryMarshal.GetReference(_buffer);
            var position = _buffered;
            for (; i < end; i++)
            {
                var value = values[i];
                position += WriteVarintAt(ref Unsafe.Add(ref buffer, position), (uint)((value << 1) ^ (value >> 31)));
            }

            _buffered = position;
        }
    }

    /// <summary>Writes <c>long</c> array items, each as a zig-zag varint.</summary>
    /// <remarks>
    /// PERF: the position is kept in a local while the buffer has room for whole values, so it is not stored to and
    /// reloaded from the writer after every value. That round trip through memory limits one-at-a-time writes on
    /// CPUs without memory renaming for such addresses (the EPYC 7543 in #27).
    /// </remarks>
    /// <param name="values">The items; write the block count first.</param>
    public void WriteLongs(scoped ReadOnlySpan<long> values)
    {
        var i = 0;
        while (i < values.Length)
        {
            var room = (_buffer.Length - _buffered) / MaxVarint64Length;
            if (room == 0)
            {
                WriteLong(values[i++]);
                continue;
            }

            var end = Math.Min(values.Length, i + room);
            ref var buffer = ref MemoryMarshal.GetReference(_buffer);
            var position = _buffered;
            for (; i < end; i++)
            {
                var value = values[i];
                position += WriteVarintAt(ref Unsafe.Add(ref buffer, position), (ulong)((value << 1) ^ (value >> 63)));
            }

            _buffered = position;
        }
    }

    /// <summary>
    /// Writes a varint at <paramref name="destination"/>, which has room for 10 bytes; returns its length. For the
    /// bulk writers: every length is inlined into their one loop, so no value is slower than a single write (#102).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteVarintAt(ref byte destination, ulong value)
    {
        if (value < 0x80)
        {
            destination = (byte)value;
            return 1;
        }

        if (value < 0x4000)
        {
            destination = (byte)(value | 0x80);
            Unsafe.Add(ref destination, 1) = (byte)(value >> 7);
            return 2;
        }

#if NET8_0_OR_GREATER
        // TEMPORARY (#102): three candidates for one benchmark run; see BulkWriteVariant. Remove before merging.
        if (FastBmi2.IsSupported)
        {
            if (BulkWriteVariant.Value == 1)
            {
                // The length-tested path: 4-byte store for 3 to 5 bytes, word for 6 to 8.
                return WriteMultiByteVarint(ref destination, value);
            }

            if (BulkWriteVariant.Value == 2 && value < 1UL << 35)
            {
                // 3 to 5 bytes without a jump on the length: the 4-group store, the fifth byte always (overwritten
                // when the value is shorter), and the length and continuation bits from two compares.
                var low = Bmi2.ParallelBitDeposit((uint)value, 0x7F7F7F7Fu);
                var length = 3 + (int)(((1UL << 21) - 1 - value) >> 63) + (int)(((1UL << 28) - 1 - value) >> 63);
                Unsafe.WriteUnaligned(ref destination, low | (0x80808080u >> ((5 - length) * 8)));
                Unsafe.Add(ref destination, 4) = (byte)(value >> 28);
                return length;
            }

            if (value < 1UL << 56)
            {
                return WriteSpreadWord(ref destination, value);
            }
        }
#endif

        return WriteMultiByteVarint(ref destination, value);
    }

    /// <summary>
    /// Writes <c>double</c> array items. On little-endian hardware this is a single copy, because Avro's
    /// encoding matches the in-memory layout.
    /// </summary>
    /// <param name="values">The items; write the block count first.</param>
    public void WriteDoubles(scoped ReadOnlySpan<double> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteRaw(MemoryMarshal.AsBytes(values));
            return;
        }

        foreach (var value in values)
        {
            WriteDouble(value);
        }
    }

    /// <summary>
    /// Writes <c>float</c> array items. On little-endian hardware this is a single copy, because Avro's
    /// encoding matches the in-memory layout.
    /// </summary>
    /// <param name="values">The items; write the block count first.</param>
    public void WriteFloats(scoped ReadOnlySpan<float> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteRaw(MemoryMarshal.AsBytes(values));
            return;
        }

        foreach (var value in values)
        {
            WriteFloat(value);
        }
    }

    /// <summary>Writes bytes as they are, with no length prefix.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteRaw(scoped ReadOnlySpan<byte> value)
    {
        if (value.Length <= _buffer.Length - _buffered)
        {
            value.CopyTo(_buffer[_buffered..]);
            _buffered += value.Length;
            return;
        }

        WriteRawSlow(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteVarint32(uint value) => WriteVarint64(value);

    /// <summary>Writes the raw (zig-zag) bits of an <c>int</c> or <c>long</c> varint.</summary>
    /// <remarks>
    /// PERF: hot path for every int, long, length, index and count.
    /// <list type="bullet">
    /// <item>One and two bytes are written inline with direct stores, each behind its own length branch. In records
    /// each field's length is usually stable, so these branches predict well. A branchless one- and two-byte store
    /// (57c987c) was slower for uniform lengths on every machine measured, and won only on randomly mixed lengths
    /// (docs/reviews/2026-09-26-branchless-varints.md).</item>
    /// <item>Where PDEP is fast (see <c>FastBmi2</c>), three to eight bytes are inline too, as one 8-byte store of
    /// the spread 7-bit groups. Out of line, the call cost more than the store: on the EPYC 7543, 3- to 8-byte
    /// values were 26-35% faster inline (#102).</item>
    /// <item>Everywhere else, and for nine and ten bytes, one out-of-line call (<see cref="WriteVarintMulti"/>). With
    /// the shift-and-mask spread inline instead, 3 and 4 bytes were up to 15% slower on an i5-3570K (no BMI2), and
    /// the larger call site made 2 bytes and Mixed1-2 13-14% slower on a Ryzen 5 3500U
    /// (docs/reviews/2026-09-28-varints-parse.md). The call is a void instance call, as before #102: returning the
    /// length to the call site instead cost 1- and 2-byte values about 7% on the EPYC.</item>
    /// </list>
    /// Measure with VarintBenchmarks (single lengths and Mixed1-10) and the record benchmarks before changing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteVarint64(ulong value)
    {
        // The fast paths need room for a whole 8-byte store. An IBufferWriter destination simply grows; near the end
        // of a fixed span the exact-size path writes only the bytes the value needs.
        if (_buffer.Length - _buffered < MaxVarint64Length)
        {
            if (_output is null)
            {
                WriteVarintExact(value);
                return;
            }

            Grow(MaxVarint64Length);
        }

        // Capacity is checked above, so these stores skip the per-element bounds check.
        ref var destination = ref At(_buffered);
        if (value < 0x80)
        {
            destination = (byte)value;
            _buffered++;
            return;
        }

        if (value < 0x4000)
        {
            // Two bytes: common enough (64 to 8191 in magnitude) to deserve direct stores.
            destination = (byte)(value | 0x80);
            Unsafe.Add(ref destination, 1) = (byte)(value >> 7);
            _buffered += 2;
            return;
        }

#if NET8_0_OR_GREATER
        if (FastBmi2.IsSupported && value < 1UL << 56)
        {
            _buffered += WriteSpreadWord(ref destination, value);
            return;
        }
#endif

        WriteVarintMulti(value);
    }

    /// <summary>A varint of 3 or more bytes, out of line so the inlined call sites stay small.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteVarintMulti(ulong value) => _buffered += WriteMultiByteVarint(ref At(_buffered), value);

    /// <summary>
    /// A varint of 3 or more bytes at <paramref name="destination"/>, which has room for 10; returns its length.
    /// Written through <c>ref</c> stores with no bounds checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteMultiByteVarint(ref byte destination, ulong value)
    {
        // Three to five bytes: spread the first four 7-bit groups into one 4-byte store, cheaper than the word path for
        // short values. Bytes past the varint are overwritten by the next value. Longer values skip this block with
        // one compare, and do not pay for building the groups (#102: computing them first made 8-byte values 11-18%
        // slower than on main).
        if (BitConverter.IsLittleEndian && value < 1UL << 35)
        {
            var low = (uint)(value & 0x7F) | ((uint)(value << 1) & 0x7F00) | ((uint)(value << 2) & 0x7F0000) | ((uint)(value << 3) & 0x7F000000);
            if (value < 1UL << 21)
            {
                Unsafe.WriteUnaligned(ref destination, low | 0x8080);
                return 3;
            }

            if (value < 1UL << 28)
            {
                Unsafe.WriteUnaligned(ref destination, low | 0x808080);
                return 4;
            }

            Unsafe.WriteUnaligned(ref destination, low | 0x80808080);
            Unsafe.Add(ref destination, 4) = (byte)(value >> 28);
            return 5;
        }

#if NET8_0_OR_GREATER
        if (value < 1UL << 56)
        {
            // 6 to 8 bytes: spread the 7-bit groups one per byte, set the continuation bits, and store the word in
            // one write.
            var length = ((63 - BitOperations.LeadingZeroCount(value)) / 7) + 1;
            var continuation = 0x8080808080808080UL & ((1UL << ((length - 1) * 8)) - 1);
            WriteWord(ref destination, SpreadVarint(value) | continuation);
            return length;
        }

        return WriteLongVarint(ref destination, value);
#else
        return WriteVarintLoop(ref destination, value);
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Writes a varint below 2^56 of 3 to 8 bytes as one 8-byte store of its 7-bit groups, spread with PDEP; returns
    /// its length. Only where PDEP is fast (FastBmi2, which implies x64 and so little-endian). Bytes past the varint
    /// are overwritten by the next value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteSpreadWord(ref byte destination, ulong value)
    {
        var length = ((63 - BitOperations.LeadingZeroCount(value)) / 7) + 1;
        var continuation = 0x8080808080808080UL & ((1UL << ((length - 1) * 8)) - 1);
        Unsafe.WriteUnaligned(ref destination, Bmi2.X64.ParallelBitDeposit(value, 0x7F7F7F7F7F7F7F7FUL) | continuation);
        return length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWord(ref byte destination, ulong word) =>
        Unsafe.WriteUnaligned(ref destination, BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word));

    /// <summary>
    /// Writes a 9- or 10-byte varint (a value of 2^56 or more): the low 56 bits as a full word of continued bytes,
    /// then bits 56-62 and bit 63. Returns the number of bytes written.
    /// </summary>
    private static int WriteLongVarint(ref byte destination, ulong value)
    {
        WriteWord(ref destination, SpreadVarint(value & ((1UL << 56) - 1)) | 0x8080808080808080UL);
        var high = value >> 56;
        if (high < 0x80)
        {
            Unsafe.Add(ref destination, 8) = (byte)high;
            return 9;
        }

        Unsafe.Add(ref destination, 8) = (byte)(high | 0x80);
        Unsafe.Add(ref destination, 9) = (byte)(high >> 7);
        return MaxVarint64Length;
    }

    /// <summary>Places the 7-bit groups of a value below 2^56 into consecutive bytes, without a loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SpreadVarint(ulong value)
    {
        if (FastBmi2.IsSupported)
        {
            return Bmi2.X64.ParallelBitDeposit(value, 0x7F7F7F7F7F7F7F7FUL);
        }

        var x = ((value & 0x00FFFFFFF0000000UL) << 4) | (value & 0x000000000FFFFFFFUL);
        x = ((x & 0x0FFFC0000FFFC000UL) << 2) | (x & 0x00003FFF00003FFFUL);
        x = ((x & 0x3F803F803F803F80UL) << 1) | (x & 0x007F007F007F007FUL);
        return x;
    }
#endif

    /// <summary>Gets the number of bytes of the varint of <paramref name="value"/>, 1 to 10.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarintLength(ulong value)
    {
#if NET8_0_OR_GREATER
        return ((63 - BitOperations.LeadingZeroCount(value | 1)) / 7) + 1;
#else
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
#endif
    }

    private static int WriteVarintLoop(Span<byte> destination, ulong value)
    {
        var position = 0;
        while (value >= 0x80)
        {
            destination[position++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[position++] = (byte)value;
        return position;
    }

#if !NET8_0_OR_GREATER
    private static int WriteVarintLoop(ref byte destination, ulong value)
    {
        var position = 0;
        while (value >= 0x80)
        {
            Unsafe.Add(ref destination, position++) = (byte)(value | 0x80);
            value >>= 7;
        }

        Unsafe.Add(ref destination, position++) = (byte)value;
        return position;
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteVarintExact(ulong value)
    {
        var length = 1;
        for (var v = value; v >= 0x80; v >>= 7)
        {
            length++;
        }

        Ensure(length);
        _buffered += WriteVarintLoop(_buffer[_buffered..], value);
    }
    /// <summary>A reference to the buffer at <paramref name="index"/>, without a bounds check; callers check capacity first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref byte At(int index) => ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int count)
    {
        if (_buffer.Length - _buffered < count)
        {
            Grow(count);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int count)
    {
        if (_output is null)
        {
            if (!_discardOverflow)
            {
                throw new AvroException($"The destination buffer is too small: {count} more byte(s) needed, {_buffer.Length - _buffered} available.");
            }

            // Keep going into scratch memory, so every write path behaves as with an IBufferWriter; the caller
            // checks Overflowed and ignores the result.
            _overflowed = true;
            _output = DiscardBufferWriter.Instance;
        }

        Flush();
        _buffer = _output.GetSpan(Math.Max(count, MinimumBufferSize));
        if (_buffer.Length < count)
        {
            throw new InvalidOperationException("The IBufferWriter<byte> returned a buffer smaller than requested.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteRawSlow(scoped ReadOnlySpan<byte> value)
    {
        if (_output is null)
        {
            Grow(value.Length);
        }

        // Fill what is left, then continue in fresh buffers, so large values never need one huge span.
        while (!value.IsEmpty)
        {
            if (_buffered == _buffer.Length)
            {
                Grow(1);
            }

            var chunk = Math.Min(value.Length, _buffer.Length - _buffered);
            value[..chunk].CopyTo(_buffer[_buffered..]);
            _buffered += chunk;
            value = value[chunk..];
        }
    }
}
