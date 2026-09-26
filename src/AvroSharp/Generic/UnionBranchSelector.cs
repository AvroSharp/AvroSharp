using System;
using System.Collections.Generic;
using System.Linq;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Selects the union branch for a value from its kind (or, for named types, its schema's full name). Shared by the
/// binary and JSON writers, so both encodings choose the same branch for the same value.
/// </summary>
internal sealed class UnionBranchSelector
{
    private readonly int[] _byKind;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly NamedSchema[] _namedBranches;
    private readonly int[] _namedIndexes;

    public UnionBranchSelector(UnionSchema schema)
    {
        _byKind = new int[(int)AvroValueKind.Fixed + 1];
        _byKind.AsSpan().Fill(-1);
        for (var i = 0; i < schema.Branches.Count; i++)
        {
            var branch = schema.Branches[i];
            if (branch is NamedSchema named)
            {
                _byName[named.FullName] = i;
            }
            else
            {
                _byKind[(int)KindOf(branch.Type)] = i;
            }
        }

        _namedBranches = [.. schema.Branches.OfType<NamedSchema>()];
        _namedIndexes = [.. _namedBranches.Select(b => _byName[b.FullName])];

        // Widening, used only when the union has no branch of the value's own kind.
        Widen(AvroValueKind.Int, AvroValueKind.Long, AvroValueKind.Float, AvroValueKind.Double);
        Widen(AvroValueKind.Long, AvroValueKind.Float, AvroValueKind.Double);
        Widen(AvroValueKind.Float, AvroValueKind.Double);
    }

    /// <summary>Returns the branch index for <paramref name="value"/>, or -1 when no branch accepts it.</summary>
    public int IndexOf(in AvroValue value)
    {
        var kind = value.Kind;
        return kind switch
        {
            AvroValueKind.Record => IndexOfNamed(value.AsRecord().Schema),
            AvroValueKind.Enum => IndexOfNamed(value.EnumSchema!),
            AvroValueKind.Fixed => IndexOfNamed(value.AsFixed().Schema),
            _ => _byKind[(int)kind],
        };
    }

    private static AvroValueKind KindOf(AvroSchemaType type) => type switch
    {
        AvroSchemaType.Null => AvroValueKind.Null,
        AvroSchemaType.Boolean => AvroValueKind.Boolean,
        AvroSchemaType.Int => AvroValueKind.Int,
        AvroSchemaType.Long => AvroValueKind.Long,
        AvroSchemaType.Float => AvroValueKind.Float,
        AvroSchemaType.Double => AvroValueKind.Double,
        AvroSchemaType.Bytes => AvroValueKind.Bytes,
        AvroSchemaType.String => AvroValueKind.String,
        AvroSchemaType.Array => AvroValueKind.Array,
        _ => AvroValueKind.Map,
    };

    // Values usually carry the very schema instance of the branch, so compare references before hashing the name.
    private int IndexOfNamed(NamedSchema schema)
    {
        var named = _namedBranches;
        for (var i = 0; i < named.Length; i++)
        {
            if (ReferenceEquals(named[i], schema))
            {
                return _namedIndexes[i];
            }
        }

        return _byName.TryGetValue(schema.FullName, out var index) ? index : -1;
    }

    private void Widen(AvroValueKind from, params AvroValueKind[] targets)
    {
        if (_byKind[(int)from] >= 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            if (_byKind[(int)target] >= 0)
            {
                _byKind[(int)from] = _byKind[(int)target];
                return;
            }
        }
    }
}
