using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AvroSharp.Schemas;

namespace AvroSharp.Confluent;

/// <summary>Whether data written with one schema means the same when read with another.</summary>
internal static class SchemaEncoding
{
    /// <summary>
    /// Gets whether <paramref name="a"/> and <paramref name="b"/> encode alike: the same Parsing Canonical Form, and
    /// the same logical types throughout. The canonical form leaves logical types out, but they decide what the
    /// encoded values mean: a decimal's precision and scale, a timestamp's or a time's unit.
    /// </summary>
    public static bool Same(AvroSchema a, AvroSchema b) =>
        a.HasSameCanonicalForm(b) && SameLogicalTypes(a, b, new HashSet<AvroSchema>(ByReference.Instance));

    // The canonical forms are equal, so both trees have the same shape.
    private static bool SameLogicalTypes(AvroSchema a, AvroSchema b, HashSet<AvroSchema> seen)
    {
        if (!SameLogicalType(a.LogicalType, b.LogicalType))
        {
            return false;
        }

        if (!seen.Add(a))
        {
            // A named type seen before: it is compared where it was defined.
            return true;
        }

        switch (a)
        {
            case RecordSchema recordA when b is RecordSchema recordB:
                for (var i = 0; i < recordA.Fields.Count; i++)
                {
                    if (!SameLogicalTypes(recordA.Fields[i].Schema, recordB.Fields[i].Schema, seen))
                    {
                        return false;
                    }
                }

                return true;
            case ArraySchema arrayA when b is ArraySchema arrayB:
                return SameLogicalTypes(arrayA.Items, arrayB.Items, seen);
            case MapSchema mapA when b is MapSchema mapB:
                return SameLogicalTypes(mapA.Values, mapB.Values, seen);
            case UnionSchema unionA when b is UnionSchema unionB:
                for (var i = 0; i < unionA.Branches.Count; i++)
                {
                    if (!SameLogicalTypes(unionA.Branches[i], unionB.Branches[i], seen))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static bool SameLogicalType(AvroLogicalType? a, AvroLogicalType? b) =>
        a is null || b is null
            ? a is null && b is null
            : a is DecimalLogicalType decimalA ? decimalA.Equals(b) : b is not DecimalLogicalType && a.Kind == b.Kind;

    private sealed class ByReference : IEqualityComparer<AvroSchema>
    {
        public static readonly ByReference Instance = new();

        public bool Equals(AvroSchema? x, AvroSchema? y) => ReferenceEquals(x, y);

        public int GetHashCode(AvroSchema obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
