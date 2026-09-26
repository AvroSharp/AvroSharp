using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace AvroSharp.Generators;

/// <summary>An immutable array compared by content, so incremental generator steps can be cached.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items) => _items = [.. items];

    public int Count => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in AsSpan())
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    public ReadOnlySpan<T> AsSpan() => _items;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
