using System.Collections.Generic;
using AvroSharp.IO;

namespace AvroSharp.Serialization.Generated;

/// <summary>
/// Writing arrays that generated types hold as <c>T[]</c> or <c>IReadOnlyList&lt;T&gt;</c>
/// (<c>AvroSharpCollectionType</c>, #18): a <see cref="List{T}"/> takes the list overloads' paths, an array is written
/// from its memory, and any other list item by item.
/// </summary>
public static partial class AvroGeneratedCode
{
    /// <summary>Writes a list of <c>boolean</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteBooleanList(ref AvroWriter writer, IReadOnlyList<bool>? items, string field)
    {
        switch (items)
        {
            case List<bool> list:
                WriteBooleanList(ref writer, list, field);
                return;
            case bool[] array:
                Begin(ref writer, array.Length);
                writer.WriteBooleans(array);
                writer.WriteBlockEnd();
                return;
        }

        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            writer.WriteBoolean(items![i]);
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>int</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteIntList(ref AvroWriter writer, IReadOnlyList<int>? items, string field)
    {
        if (items is List<int> list)
        {
            WriteIntList(ref writer, list, field);
            return;
        }

#if NET8_0_OR_GREATER
        if (items is int[] array)
        {
            Begin(ref writer, array.Length);
            writer.WriteInts(array);
            writer.WriteBlockEnd();
            return;
        }
#endif

        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            writer.WriteInt(items![i]);
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>long</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteLongList(ref AvroWriter writer, IReadOnlyList<long>? items, string field)
    {
        if (items is List<long> list)
        {
            WriteLongList(ref writer, list, field);
            return;
        }

#if NET8_0_OR_GREATER
        if (items is long[] array)
        {
            Begin(ref writer, array.Length);
            writer.WriteLongs(array);
            writer.WriteBlockEnd();
            return;
        }
#endif

        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            writer.WriteLong(items![i]);
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>float</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteFloatList(ref AvroWriter writer, IReadOnlyList<float>? items, string field)
    {
        switch (items)
        {
            case List<float> list:
                WriteFloatList(ref writer, list, field);
                return;
            case float[] array:
                Begin(ref writer, array.Length);
                writer.WriteFloats(array);
                writer.WriteBlockEnd();
                return;
        }

        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            writer.WriteFloat(items![i]);
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list of <c>double</c> as an array.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    public static void WriteDoubleList(ref AvroWriter writer, IReadOnlyList<double>? items, string field)
    {
        switch (items)
        {
            case List<double> list:
                WriteDoubleList(ref writer, list, field);
                return;
            case double[] array:
                Begin(ref writer, array.Length);
                writer.WriteDoubles(array);
                writer.WriteBlockEnd();
                return;
        }

        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            writer.WriteDouble(items![i]);
        }

        writer.WriteBlockEnd();
    }

    /// <summary>Writes a list as an array, each item with <typeparamref name="TSerializer"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TSerializer">Its serializer.</typeparam>
    /// <param name="writer">The destination.</param>
    /// <param name="items">The items.</param>
    /// <param name="field">The field, as <c>Record.field</c>, for errors.</param>
    /// <param name="depth">The nesting depth for the items.</param>
    public static void WriteList<T, TSerializer>(ref AvroWriter writer, IReadOnlyList<T>? items, string field, int depth)
        where TSerializer : struct, IAvroValueSerializer<T>
    {
        if (items is List<T> list)
        {
            WriteList<T, TSerializer>(ref writer, list, field, depth);
            return;
        }

        var serializer = default(TSerializer);
        for (int i = 0, count = Begin(ref writer, items, field); i < count; i++)
        {
            var item = items![i];
            if (item is null)
            {
                throw NullValue(field);
            }

            serializer.Write(ref writer, item, depth);
        }

        writer.WriteBlockEnd();
    }

    // Writes the block count of a non-empty array; returns the count.
    private static int Begin<T>(ref AvroWriter writer, IReadOnlyList<T>? items, string field)
    {
        var count = items?.Count ?? throw NullValue(field);
        return Begin(ref writer, count);
    }

    private static int Begin(ref AvroWriter writer, int count)
    {
        if (count > 0)
        {
            writer.WriteBlockCount(count);
        }

        return count;
    }
}
