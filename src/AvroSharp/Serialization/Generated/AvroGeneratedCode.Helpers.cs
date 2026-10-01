using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif
using System.Threading;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

#pragma warning disable CA1002, MA0016 // Generated types expose List<T> and Dictionary<TKey, TValue>; these helpers fill and write them.

namespace AvroSharp.Serialization.Generated;

// Helpers that keep generated code small: a field that is a nullable primitive, a collection of primitives or records,
// or a nullable record is one call instead of an inlined switch or loop. They are small and marked for inlining, and
// the collection helpers take struct serializers, so the JIT specializes them per type and the machine code stays that of
// the inlined version.
public static partial class AvroGeneratedCode
{
    // --- Nullable unions: ["null", T] or [T, "null"]; valueIndex is the branch of T (a constant in generated code) ---

    /// <summary>Reads a union of <c>null</c> and <c>boolean</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool? ReadNullableBoolean(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadBoolean() : null;

    /// <summary>Reads a union of <c>null</c> and <c>int</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ReadNullableInt(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadInt() : null;

    /// <summary>Reads a union of <c>null</c> and <c>long</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ReadNullableLong(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadLong() : null;

    /// <summary>Reads a union of <c>null</c> and <c>float</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float? ReadNullableFloat(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadFloat() : null;

    /// <summary>Reads a union of <c>null</c> and <c>double</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double? ReadNullableDouble(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadDouble() : null;

    /// <summary>Reads a union of <c>null</c> and <c>string</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? ReadNullableString(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadString() : null;

    /// <summary>Reads a union of <c>null</c> and <c>bytes</c>.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[]? ReadNullableBytes(ref AvroReader reader, int valueIndex) => IsValue(ref reader, valueIndex) ? reader.ReadBytes() : null;

    /// <summary>Reads a union of <c>null</c> and a record (or any type with a serializer).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="reader">The source.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    /// <param name="depth">The nesting depth for the value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? ReadNullable<T, TSerializer>(ref AvroReader reader, int valueIndex, int depth)
        where T : class
        where TSerializer : struct, IAvroValueSerializer<T> =>
        IsValue(ref reader, valueIndex) ? default(TSerializer).Read(ref reader, depth) : null;

    /// <summary>Writes a union of <c>null</c> and <c>boolean</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableBoolean(ref AvroWriter writer, bool? value, int valueIndex)
    {
        writer.WriteUnionIndex(value.HasValue ? valueIndex : 1 - valueIndex);
        if (value.HasValue)
        {
            writer.WriteBoolean(value.GetValueOrDefault());
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>int</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableInt(ref AvroWriter writer, int? value, int valueIndex)
    {
        writer.WriteUnionIndex(value.HasValue ? valueIndex : 1 - valueIndex);
        if (value.HasValue)
        {
            writer.WriteInt(value.GetValueOrDefault());
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>long</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableLong(ref AvroWriter writer, long? value, int valueIndex)
    {
        writer.WriteUnionIndex(value.HasValue ? valueIndex : 1 - valueIndex);
        if (value.HasValue)
        {
            writer.WriteLong(value.GetValueOrDefault());
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>float</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableFloat(ref AvroWriter writer, float? value, int valueIndex)
    {
        writer.WriteUnionIndex(value.HasValue ? valueIndex : 1 - valueIndex);
        if (value.HasValue)
        {
            writer.WriteFloat(value.GetValueOrDefault());
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>double</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableDouble(ref AvroWriter writer, double? value, int valueIndex)
    {
        writer.WriteUnionIndex(value.HasValue ? valueIndex : 1 - valueIndex);
        if (value.HasValue)
        {
            writer.WriteDouble(value.GetValueOrDefault());
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>string</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableString(ref AvroWriter writer, string? value, int valueIndex)
    {
        writer.WriteUnionIndex(value is null ? 1 - valueIndex : valueIndex);
        if (value is not null)
        {
            writer.WriteString(value);
        }
    }

    /// <summary>Writes a union of <c>null</c> and <c>bytes</c>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullableBytes(ref AvroWriter writer, byte[]? value, int valueIndex)
    {
        writer.WriteUnionIndex(value is null ? 1 - valueIndex : valueIndex);
        if (value is not null)
        {
            writer.WriteBytes(value);
        }
    }

    /// <summary>Writes a union of <c>null</c> and a record (or any type with a serializer).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <param name="valueIndex">The branch index of the non-null type (0 or 1).</param>
    /// <param name="depth">The nesting depth for the value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNullable<T, TSerializer>(ref AvroWriter writer, T? value, int valueIndex, int depth)
        where T : class
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        writer.WriteUnionIndex(value is null ? 1 - valueIndex : valueIndex);
        if (value is not null)
        {
            default(TSerializer).Write(ref writer, value, depth);
        }
    }

    // Reads the index of a two-branch union: true for the value's branch, false for null, and an error otherwise.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValue(ref AvroReader reader, int valueIndex)
    {
        var index = reader.ReadUnionIndex();
        if (index == valueIndex)
        {
            return true;
        }

        if (index != 1 - valueIndex)
        {
            throw InvalidUnionIndex(index, 2);
        }

        return false;
    }

    // --- Collections ---

    /// <summary>
    /// Reads an array into a list: <paramref name="reuse"/>, cleared, when there is one, otherwise a new list. Items
    /// are added as they are read, so a hostile block count cannot allocate ahead of the input.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    /// <param name="minimumItemSize">The smallest encoded size of one item.</param>
    /// <param name="depth">The nesting depth for the items.</param>
    public static List<T> ReadList<T, TSerializer>(ref AvroReader reader, List<T>? reuse, int minimumItemSize, int depth)
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        var list = Reuse(reuse);
        var serializer = default(TSerializer);
        int count;
        while ((count = ReadBlockItemCount(ref reader, minimumItemSize, list.Count)) != 0)
        {
            Reserve(list, count);
            for (var i = 0; i < count; i++)
            {
                list.Add(serializer.Read(ref reader, depth));
            }
        }

        return list;
    }

    /// <summary>Writes a list as an array; a <see langword="null"/> list or item throws, naming <paramref name="field"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    /// <param name="depth">The nesting depth for the items.</param>
    public static void WriteList<T, TSerializer>(ref AvroWriter writer, List<T>? items, string field, int depth)
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        if (items is null)
        {
            throw NullValue(field);
        }

        if (items.Count > 0)
        {
            writer.WriteBlockCount(items.Count);
            var serializer = default(TSerializer);
#if NET8_0_OR_GREATER
            foreach (var item in CollectionsMarshal.AsSpan(items))
#else
            foreach (var item in items)
#endif
            {
                if (item is null)
                {
                    throw NullValue(field);
                }

                serializer.Write(ref writer, item, depth);
            }
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Reads a map into a dictionary: <paramref name="reuse"/>, cleared, when there is one, otherwise a new one (ordinal keys).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A dictionary to fill again, or <see langword="null"/>.</param>
    /// <param name="minimumEntrySize">The smallest encoded size of one entry, its key included.</param>
    /// <param name="depth">The nesting depth for the values.</param>
    public static Dictionary<string, T> ReadMap<T, TSerializer>(ref AvroReader reader, Dictionary<string, T>? reuse, int minimumEntrySize, int depth)
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        // The first block's count sizes a new dictionary, as in the generic reader.
        var count = ReadBlockItemCount(ref reader, minimumEntrySize, 0);
        Dictionary<string, T> map;
        if (reuse is null)
        {
            map = new Dictionary<string, T>(InitialCapacity(count), StringComparer.Ordinal);
        }
        else
        {
            map = reuse;
            map.Clear();
        }

        var serializer = default(TSerializer);
        while (count != 0)
        {
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                map[key] = serializer.Read(ref reader, depth);
            }

            count = ReadBlockItemCount(ref reader, minimumEntrySize, map.Count);
        }

        return map;
    }

    /// <summary>Writes a dictionary as a map; a <see langword="null"/> dictionary or value throws, naming <paramref name="field"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="writer">The destination.</param>
    /// <param name="map">The entries.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    /// <param name="depth">The nesting depth for the values.</param>
    public static void WriteMap<T, TSerializer>(ref AvroWriter writer, Dictionary<string, T>? map, string field, int depth)
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        if (map is null)
        {
            throw NullValue(field);
        }

        if (map.Count > 0)
        {
            writer.WriteBlockCount(map.Count);
            var serializer = default(TSerializer);
            foreach (var entry in map)
            {
                if (entry.Value is null)
                {
                    throw NullValue(field);
                }

                writer.WriteString(entry.Key);
                serializer.Write(ref writer, entry.Value, depth);
            }
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Reads an array of <c>boolean</c> into a list, a block at a time.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    public static List<bool> ReadBooleanList(ref AvroReader reader, List<bool>? reuse) => ReadPrimitiveList<bool, AvroBooleanSerializer>(ref reader, reuse, 1);

    /// <summary>Reads an array of <c>int</c> into a list; runs of one-byte values are decoded together.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    public static List<int> ReadIntList(ref AvroReader reader, List<int>? reuse) => ReadPrimitiveList<int, AvroIntSerializer>(ref reader, reuse, 1);

    /// <summary>Reads an array of <c>long</c> into a list; runs of one-byte values are decoded together.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    public static List<long> ReadLongList(ref AvroReader reader, List<long>? reuse) => ReadPrimitiveList<long, AvroLongSerializer>(ref reader, reuse, 1);

    /// <summary>Reads an array of <c>float</c> into a list, one copy per block on little-endian hardware.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    public static List<float> ReadFloatList(ref AvroReader reader, List<float>? reuse) => ReadPrimitiveList<float, AvroFloatSerializer>(ref reader, reuse, sizeof(float));

    /// <summary>Reads an array of <c>double</c> into a list, one copy per block on little-endian hardware.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="reuse">A list to fill again, or <see langword="null"/>.</param>
    public static List<double> ReadDoubleList(ref AvroReader reader, List<double>? reuse) => ReadPrimitiveList<double, AvroDoubleSerializer>(ref reader, reuse, sizeof(double));

    /// <summary>Writes a list of <c>boolean</c> as an array, one copy per block.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteBooleanList(ref AvroWriter writer, List<bool>? items, string field)
    {
        var count = Count(items, field);
        if (count > 0)
        {
            writer.WriteBlockCount(count);
#if NET8_0_OR_GREATER
            writer.WriteBooleans(CollectionsMarshal.AsSpan(items));
#else
            writer.WriteBooleans(items!.ToArray());
#endif
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>int</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteIntList(ref AvroWriter writer, List<int>? items, string field)
    {
        var count = Count(items, field);
        if (count > 0)
        {
            writer.WriteBlockCount(count);
#if NET8_0_OR_GREATER
            writer.WriteInts(CollectionsMarshal.AsSpan(items));
#else
            foreach (var item in items!)
            {
                writer.WriteInt(item);
            }
#endif
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>long</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteLongList(ref AvroWriter writer, List<long>? items, string field)
    {
        var count = Count(items, field);
        if (count > 0)
        {
            writer.WriteBlockCount(count);
#if NET8_0_OR_GREATER
            writer.WriteLongs(CollectionsMarshal.AsSpan(items));
#else
            foreach (var item in items!)
            {
                writer.WriteLong(item);
            }
#endif
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>float</c> as an array, one copy on little-endian hardware.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteFloatList(ref AvroWriter writer, List<float>? items, string field)
    {
        var count = Count(items, field);
        if (count > 0)
        {
            writer.WriteBlockCount(count);
#if NET8_0_OR_GREATER
            writer.WriteFloats(CollectionsMarshal.AsSpan(items));
#else
            writer.WriteFloats(items!.ToArray());
#endif
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>double</c> as an array, one copy on little-endian hardware.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteDoubleList(ref AvroWriter writer, List<double>? items, string field)
    {
        var count = Count(items, field);
        if (count > 0)
        {
            writer.WriteBlockCount(count);
#if NET8_0_OR_GREATER
            writer.WriteDoubles(CollectionsMarshal.AsSpan(items));
#else
            writer.WriteDoubles(items!.ToArray());
#endif
        }

        writer.WriteBlockEnd();
    }

    // The primitive lists: each block is decoded straight into the list's memory on net8+ (bulk varint and copy paths).
    private static List<T> ReadPrimitiveList<T, TSerializer>(ref AvroReader reader, List<T>? reuse, int minimumItemSize)
        where T : unmanaged
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        var list = Reuse(reuse);
        int count;
        while ((count = ReadBlockItemCount(ref reader, minimumItemSize, list.Count)) != 0)
        {
#if NET8_0_OR_GREATER
            // The block count was checked against the remaining input, which bounds this allocation.
            var start = list.Count;
            CollectionsMarshal.SetCount(list, start + count);
            ReadBulk(ref reader, CollectionsMarshal.AsSpan(list).Slice(start, count));
#else
            Reserve(list, count);
            var serializer = default(TSerializer);
            for (var i = 0; i < count; i++)
            {
                list.Add(serializer.Read(ref reader, 0));
            }
#endif
        }

        return list;
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadBulk<T>(ref AvroReader reader, Span<T> destination)
        where T : unmanaged
    {
        if (typeof(T) == typeof(long))
        {
            reader.ReadLongs(MemoryMarshal.Cast<T, long>(destination));
        }
        else if (typeof(T) == typeof(int))
        {
            reader.ReadInts(MemoryMarshal.Cast<T, int>(destination));
        }
        else if (typeof(T) == typeof(double))
        {
            reader.ReadDoubles(MemoryMarshal.Cast<T, double>(destination));
        }
        else if (typeof(T) == typeof(float))
        {
            reader.ReadFloats(MemoryMarshal.Cast<T, float>(destination));
        }
        else
        {
            reader.ReadBooleans(MemoryMarshal.Cast<T, bool>(destination));
        }
    }

#endif

    // The number of items to write; a missing list is an error that names the field.
    private static int Count<T>(List<T>? items, string field) => items?.Count ?? throw NullValue(field);

    private static List<T> Reuse<T>(List<T>? reuse)
    {
        if (reuse is null)
        {
            return [];
        }

        reuse.Clear();
        return reuse;
    }

    // A new list's first block reserves up to PreallocationLimit items; later blocks grow as items arrive.
    private static void Reserve<T>(List<T> list, int count)
    {
        if (list.Count == 0 && list.Capacity < count)
        {
            list.Capacity = InitialCapacity(count);
        }
    }

    // --- Checks and conversions ---

    /// <summary>Checks an enum value before it is written: C# enums hold any number, Avro only the symbols' ordinals.</summary>
    /// <param name="ordinal">The value, as its number.</param>
    /// <param name="symbolCount">The number of symbols.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CheckEnumOrdinal(int ordinal, int symbolCount, string field) =>
        (uint)ordinal < (uint)symbolCount ? ordinal : throw EnumOutOfRange(ordinal, symbolCount, field);

    /// <summary>Converts a <c>date</c> (days since 1970-01-01) to a day number (days since 0001-01-01), checking its range.</summary>
    /// <param name="days">The days since the Unix epoch.</param>
    public static int DayNumberFromDays(int days)
    {
        // DateOnly holds day numbers 0 (0001-01-01) to 3,652,058 (9999-12-31); 1970-01-01 is day 719,162.
        const int EpochDayNumber = 719_162;
        const int MaxDayNumber = 3_652_058;
        var dayNumber = (long)days + EpochDayNumber;
        return dayNumber is >= 0 and <= MaxDayNumber
            ? (int)dayNumber
            : throw new AvroDataException($"The date {days} days from 1970-01-01 is outside the range of DateOnly." + AvroLogicalValues.OutOfRangeHint);
    }

    /// <summary>Writes a <c>fixed</c> value held as a <c>byte[]</c> (the attribute-driven generator's <c>[AvroFixed]</c>), checking its length.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The bytes.</param>
    /// <param name="size">The fixed type's size.</param>
    /// <param name="field">The field's full name, for the error message.</param>
    /// <exception cref="AvroException">The value is <see langword="null"/> or not <paramref name="size"/> bytes long.</exception>
    public static void WriteFixedBytes(ref AvroWriter writer, byte[]? value, int size, string field)
    {
        if (value is null || value.Length != size)
        {
            throw new AvroException($"Field '{field}': a fixed value of {size} bytes cannot be {(value is null ? "null" : $"{value.Length} bytes")}.");
        }

        writer.WriteFixed(value);
    }

    /// <summary>Reads the bytes of a <c>fixed</c> value into a new array.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="size">The fixed type's size.</param>
    public static byte[] ReadFixedBytes(ref AvroReader reader, int size)
    {
#if NET8_0_OR_GREATER
        var bytes = GC.AllocateUninitializedArray<byte>(size);
#else
        var bytes = new byte[size];
#endif
        reader.ReadFixed(bytes);
        return bytes;
    }

    /// <summary>Creates the error for a value whose type does not match the field it is put into.</summary>
    /// <param name="value">The value.</param>
    /// <param name="schema">The record's schema.</param>
    /// <param name="fieldPos">The field's position.</param>
    /// <param name="expectedType">The field's C# type.</param>
    public static AvroException PutTypeMismatch(object? value, AvroSchema schema, int fieldPos, string expectedType)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var record = (RecordSchema)schema;
        return new($"Field '{record.FullName}.{record.Fields[fieldPos].Name}' holds {expectedType}; a {(value is null ? "null" : "value of type " + value.GetType().FullName)} cannot be put into it.");
    }

    private static AvroException EnumOutOfRange(int ordinal, int symbolCount, string field) =>
        new($"Field '{field}': the enum value {ordinal} is not a symbol ({symbolCount} symbols).");

    // --- Schema resolution ---

    /// <summary>
    /// Gets the plan for reading a generated record from data of another version of its schema, or <see langword="null"/>
    /// when <paramref name="writerSchema"/> is not a record of the same name (then use <see cref="ResolveToReaderEncoding"/>).
    /// Plans are built once per pair of schemas, and <paramref name="cache"/> (a static field of the generated type)
    /// remembers the last writer schema, so a stream of values written with one schema looks the plan up once.
    /// </summary>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <param name="readerSchema">The generated type's schema.</param>
    /// <param name="cache">The generated type's cache.</param>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved.</exception>
    public static AvroRecordPlan? GetRecordPlan(AvroSchema writerSchema, AvroSchema readerSchema, ref AvroPlanCache? cache)
    {
        var entry = Volatile.Read(ref cache);
        if (entry is not null && ReferenceEquals(entry.WriterSchema, writerSchema))
        {
            return entry.Plan;
        }

        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        var plan = GenericDatumReader.GetRecordPlan(writerSchema, readerSchema);
        Volatile.Write(ref cache, new AvroPlanCache(writerSchema, plan));
        return plan;
    }

    // --- Writing into caller memory ---

    /// <summary>
    /// Creates a writer over <paramref name="destination"/> for a Try* write: running out of room does not throw;
    /// <see cref="EndTryWrite"/> reports it.
    /// </summary>
    /// <param name="destination">The caller's memory.</param>
    public static AvroWriter BeginTryWrite(Span<byte> destination) => new(destination, discardOverflow: true);

    /// <summary>Completes a write begun by <see cref="BeginTryWrite"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="bytesWritten">The bytes written, or 0 when the destination was too small.</param>
    /// <returns><see langword="true"/> when the value fit.</returns>
    public static bool EndTryWrite(ref AvroWriter writer, out int bytesWritten)
    {
        if (writer.Overflowed)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = (int)writer.BytesWritten;
        return true;
    }
}

/// <summary>The last writer schema a generated type resolved, and its plan (see <see cref="AvroGeneratedCode.GetRecordPlan(AvroSchema, AvroSchema, ref AvroPlanCache?)"/>).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class AvroPlanCache
{
    internal AvroPlanCache(AvroSchema writerSchema, AvroRecordPlan? plan)
    {
        WriterSchema = writerSchema;
        Plan = plan;
    }

    internal AvroSchema WriterSchema { get; }

    internal AvroRecordPlan? Plan { get; }
}
