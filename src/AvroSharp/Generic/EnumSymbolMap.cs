using System.Runtime.CompilerServices;
using System.Threading;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// The ordinal of an enum value's symbol in the schema it is written as, for the generic writers. A value's ordinal is its
/// position in its own schema's symbols, which another version of the enum may order differently, or lack: written as is,
/// it was another symbol, or none. A value of the target schema itself is written as is. For another schema of the same
/// name, its symbols are mapped once, and the map is kept for the next value of that schema: data read with one copy of a
/// schema and written with another (parsed separately, or another version) costs a reference check and an array lookup.
/// </summary>
/// <remarks>
/// Thread-safe: the writers are shared. The last map is replaced as a whole, so a reader sees an old or a new one. It
/// keeps its source schema alive while the writer lives: one schema per enum, which a weak reference would not save
/// enough to be worth a check on every value.
/// </remarks>
internal sealed class EnumSymbolMap(EnumSchema target)
{
    private Mapping? _last;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Ordinal(in AvroValue value)
    {
        if (ReferenceEquals(value.Reference, target))
        {
            return (int)value.Bits;
        }

        var last = Volatile.Read(ref _last);
        return last is not null && ReferenceEquals(value.Reference, last.Source) ? last.Map(value) : Map(value);
    }

    private int Map(in AvroValue value)
    {
        if (value.EnumSchema is not { } source || source.Name != target.Name)
        {
            throw new AvroException($"A {value.Kind} value cannot be written as {target.CanonicalForm}.");
        }

        var ordinals = new int[source.Symbols.Count];
        for (var i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = target.TryGetOrdinal(source.Symbols[i], out var ordinal) ? ordinal : -1;
        }

        var mapping = new Mapping(source, target, ordinals);
        Volatile.Write(ref _last, mapping);
        return mapping.Map(value);
    }

    private sealed class Mapping(EnumSchema source, EnumSchema target, int[] ordinals)
    {
        public EnumSchema Source { get; } = source;

        public int Map(in AvroValue value)
        {
            var ordinal = ordinals[(int)value.Bits];
            return ordinal >= 0
                ? ordinal
                : throw new AvroException($"The symbol '{Source.Symbols[(int)value.Bits]}' is not in the enum {target.FullName} it is written as.");
        }
    }
}
