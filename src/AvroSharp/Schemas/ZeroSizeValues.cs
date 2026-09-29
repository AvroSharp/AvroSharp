namespace AvroSharp.Schemas;

/// <summary>
/// What a value that encodes to no bytes costs to read: <c>null</c>, <c>fixed</c> of size 0, and records whose fields
/// are all such values. The input cannot bound how many of them it declares, so readers budget them (#129): by count
/// alone, a record of many <c>null</c> fields would be free, although reading one creates a value for every field.
/// </summary>
internal static class ZeroSizeValues
{
    /// <summary>
    /// Gets how many values reading one value of <paramref name="schema"/> creates when the value takes no bytes: one,
    /// plus one for each field of each record in it. Zero when every value of the schema takes at least one byte.
    /// </summary>
    public static long Count(AvroSchema schema) => Count(schema, depth: 0);

    private static long Count(AvroSchema schema, int depth)
    {
        switch (schema)
        {
            case FixedSchema fixedSchema:
                return fixedSchema.Size == 0 ? 1 : 0;
            case RecordSchema record:
                // A record that holds itself directly has no finite value; the depth bound ends that recursion.
                if (depth > 64)
                {
                    return 0;
                }

                var total = 1L;
                foreach (var field in record.Fields)
                {
                    var fieldCount = Count(field.Schema, depth + 1);
                    if (fieldCount == 0)
                    {
                        return 0;
                    }

                    total = Add(total, fieldCount);
                }

                return total;
            default:
                return schema.Type == AvroSchemaType.Null ? 1 : 0;
        }
    }

    /// <summary>Adds two costs, saturating at <see cref="long.MaxValue"/>.</summary>
    public static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
