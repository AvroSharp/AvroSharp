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

    private readonly IBufferWriter<byte>? _output;
    private Span<byte> _buffer;
    private int _buffered;
    private long _committed;

    /// <summary>Initializes a writer that appends to <paramref name="output"/>.</summary>
    /// <param name="output">The destination; call <see cref="Flush"/> to commit the final bytes to it.</param>
    public AvroWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _buffer = default;
        _buffered = 0;
        _committed = 0;
    }

    /// <summary>Initializes a writer over a fixed destination. Writing more than it holds throws <see cref="AvroException"/>.</summary>
    /// <param name="destination">The buffer to write into.</param>
    public AvroWriter(Span<byte> destination)
    {
        _output = null;
        _buffer = destination;
        _buffered = 0;
        _committed = 0;
    }

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

        // Counting first lets the length prefix be written before the bytes are encoded directly into the
        // destination; both passes are vectorized by the runtime.
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
    /// <item>One and two bytes are written inline, and three and four bytes with direct stores, each behind its own
    /// length branch. In records each field's length is usually stable, so these branches predict well. A
    /// branchless one- and two-byte store (57c987c) was slower for uniform lengths on every machine measured, and
    /// won only on randomly mixed lengths (docs/reviews/2026-09-26-branchless-varints.md).</item>
    /// <item>Five to eight bytes (net8+) are one 8-byte store; the 7-bit groups are spread with BMI2 PDEP where it is
    /// fast (see <c>FastBmi2</c>), or with shifts and masks otherwise.</item>
    /// <item>Nine and ten bytes add one or two bytes after the word.</item>
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

        WriteVarintMulti(value);
    }

    /// <summary>A varint of 3 or more bytes; kept out of line so the inlined call sites stay small.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteVarintMulti(ulong value)
    {
        var buffer = _buffer[_buffered..];

        // Three or four bytes: direct stores, cheaper than the word path for short values.
        if (value < 1UL << 21)
        {
            buffer[0] = (byte)(value | 0x80);
            buffer[1] = (byte)((value >> 7) | 0x80);
            buffer[2] = (byte)(value >> 14);
            _buffered += 3;
            return;
        }

        if (value < 1UL << 28)
        {
            buffer[0] = (byte)(value | 0x80);
            buffer[1] = (byte)((value >> 7) | 0x80);
            buffer[2] = (byte)((value >> 14) | 0x80);
            buffer[3] = (byte)(value >> 21);
            _buffered += 4;
            return;
        }

#if NET8_0_OR_GREATER
        if (value < 1UL << 56)
        {
            // 5 to 8 bytes: spread the 7-bit groups one per byte, set the continuation bits, and store the word in
            // one write. Bytes past the varint are overwritten by the next value.
            var length = ((63 - BitOperations.LeadingZeroCount(value)) / 7) + 1;
            var continuation = 0x8080808080808080UL & ((1UL << ((length - 1) * 8)) - 1);
            WriteWord(buffer, SpreadVarint(value) | continuation);
            _buffered += length;
            return;
        }

        _buffered += WriteLongVarint(buffer, value);
#else
        _buffered += WriteVarintLoop(buffer, value);
#endif
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWord(Span<byte> buffer, ulong word)
    {
        if (BitConverter.IsLittleEndian)
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer), word);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, word);
        }
    }
#endif
#if NET8_0_OR_GREATER
    /// <summary>
    /// Writes a 9- or 10-byte varint (a value of 2^56 or more): the low 56 bits as a full word of continued bytes,
    /// then bits 56-62 and bit 63. Returns the number of bytes written.
    /// </summary>
    private static int WriteLongVarint(Span<byte> buffer, ulong value)
    {
        WriteWord(buffer, SpreadVarint(value & ((1UL << 56) - 1)) | 0x8080808080808080UL);
        var high = value >> 56;
        if (high < 0x80)
        {
            buffer[8] = (byte)high;
            return 9;
        }

        buffer[8] = (byte)(high | 0x80);
        buffer[9] = (byte)(high >> 7);
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
            throw new AvroException($"The destination buffer is too small: {count} more byte(s) needed, {_buffer.Length - _buffered} available.");
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
