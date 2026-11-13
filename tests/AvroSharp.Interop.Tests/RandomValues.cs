using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Interop.Tests;

/// <summary>Random <see cref="AvroValue"/> data for a schema, for property-based tests: the public <see cref="AvroRandomData"/>.</summary>
internal sealed class RandomValues(int seed)
{
    private readonly AvroRandomData _random = new(seed);

    /// <summary>Creates a value, or <see langword="null"/> when the schema has no finite value (a record that contains itself outside a union, array or map).</summary>
    public AvroValue? TryCreate(AvroSchema schema)
    {
        try
        {
            return _random.Next(schema);
        }
        catch (AvroException)
        {
            return null;
        }
    }
}
