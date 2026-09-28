using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvroSharp.Generic;

/// <summary>
/// An array of <c>boolean</c>, <c>int</c>, <c>long</c>, <c>float</c> or <c>double</c> items stored as the primitive
/// values themselves, not one <see cref="AvroValue"/> each. It is still an <see cref="IReadOnlyList{T}"/> of values,
/// so <see cref="AvroValue.AsArray"/> works as for any other array.
/// </summary>
internal abstract class PrimitiveArray : IReadOnlyList<AvroValue>
{
    public abstract int Count { get; }

    public abstract AvroValue this[int index] { get; }

    /// <summary>Compares the items with another typed array of the same element type, by bit pattern (as <see cref="AvroValue.Equals(AvroValue)"/> does).</summary>
    public abstract bool? ItemsEqual(PrimitiveArray other);

    public IEnumerator<AvroValue> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal abstract class PrimitiveArray<T>(ReadOnlyMemory<T> items) : PrimitiveArray
    where T : unmanaged
{
    public ReadOnlyMemory<T> Items { get; } = items;

    public override int Count => Items.Length;

    public override AvroValue this[int index] =>
        (uint)index < (uint)Items.Length ? ToValue(Items.Span[index]) : throw new ArgumentOutOfRangeException(nameof(index));

    public override bool? ItemsEqual(PrimitiveArray other) => other is PrimitiveArray<T> typed
        ? MemoryMarshal.AsBytes(Items.Span).SequenceEqual(MemoryMarshal.AsBytes(typed.Items.Span))
        : null;

    protected abstract AvroValue ToValue(T value);
}

internal sealed class BooleanArray(ReadOnlyMemory<bool> items) : PrimitiveArray<bool>(items)
{
    protected override AvroValue ToValue(bool value) => value;
}

internal sealed class Int32Array(ReadOnlyMemory<int> items) : PrimitiveArray<int>(items)
{
    protected override AvroValue ToValue(int value) => value;
}

internal sealed class Int64Array(ReadOnlyMemory<long> items) : PrimitiveArray<long>(items)
{
    protected override AvroValue ToValue(long value) => value;
}

internal sealed class SingleArray(ReadOnlyMemory<float> items) : PrimitiveArray<float>(items)
{
    protected override AvroValue ToValue(float value) => value;
}

internal sealed class DoubleArray(ReadOnlyMemory<double> items) : PrimitiveArray<double>(items)
{
    protected override AvroValue ToValue(double value) => value;
}
