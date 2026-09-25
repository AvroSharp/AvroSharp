using System;
using System.Collections.Generic;
using System.Linq;

namespace AvroSharp.Schemas;

/// <summary>A union schema: a value matches exactly one of the branches.</summary>
public sealed class UnionSchema : AvroSchema
{
    /// <summary>Initializes a union schema.</summary>
    /// <param name="branches">The branches, in order (the order defines each branch's index).</param>
    /// <exception cref="AvroSchemaException">
    /// A branch is itself a union, or two branches have the same type (named types must differ by full name).
    /// </exception>
    public UnionSchema(IEnumerable<AvroSchema> branches)
        : base(AvroSchemaType.Union, logicalType: null, properties: null)
    {
        ArgumentNullException.ThrowIfNull(branches);

        var array = branches.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in array)
        {
            if (branch is null)
            {
                throw new AvroSchemaException("A union branch cannot be null.");
            }

            if (branch.Type == AvroSchemaType.Union)
            {
                throw new AvroSchemaException("Unions may not immediately contain other unions.");
            }

            // Unnamed types are identified by their type (a logical type does not make a branch distinct);
            // named types by their full name.
            var key = branch is NamedSchema named ? named.FullName : AvroNames.GetTypeName(branch.Type);
            if (!seen.Add(key))
            {
                throw new AvroSchemaException($"Unions may not contain more than one '{key}' branch.");
            }
        }

        Branches = array;
    }

    /// <summary>Gets the branches, in order.</summary>
    public IReadOnlyList<AvroSchema> Branches { get; }
}
