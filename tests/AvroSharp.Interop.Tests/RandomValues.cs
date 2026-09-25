using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Interop.Tests;

/// <summary>Random <see cref="AvroValue"/> data for a schema, for property-based tests.</summary>
internal sealed class RandomValues(int seed)
{
    private const int MaxDepth = 6;

    private readonly Random _random = new(seed);

    /// <summary>Creates a value, or <see langword="null"/> when the schema has no finite value (a record that contains itself without a union).</summary>
    public AvroValue? TryCreate(AvroSchema schema)
    {
        try
        {
            return Create(schema, 0);
        }
        catch (UninhabitedException)
        {
            return null;
        }
    }

    private AvroValue Create(AvroSchema schema, int depth)
    {
        switch (schema)
        {
            case RecordSchema record:
                if (depth > MaxDepth)
                {
                    throw new UninhabitedException();
                }

                var value = new GenericRecord(record);
                foreach (var field in record.Fields)
                {
                    value[field.Position] = Create(field.Schema, depth + 1);
                }

                return value;
            case EnumSchema enumSchema:
                return AvroValue.FromEnum(enumSchema, _random.Next(enumSchema.Symbols.Count));
            case FixedSchema fixedSchema:
                return new GenericFixed(fixedSchema, Bytes(fixedSchema.Size));
            case ArraySchema array:
                return AvroValue.FromArray(Enumerable.Range(0, depth > MaxDepth ? 0 : _random.Next(0, 6)).Select(_ => Create(array.Items, depth + 1)).ToList());
            case MapSchema map:
                var entries = new Dictionary<string, AvroValue>(StringComparer.Ordinal);
                for (var i = depth > MaxDepth ? 0 : _random.Next(0, 5); i > 0; i--)
                {
                    entries[Text(_random.Next(0, 8))] = Create(map.Values, depth + 1);
                }

                return AvroValue.FromMap(entries);
            case UnionSchema union:
                return CreateUnion(union, depth);
            default:
                return schema.Type switch
                {
                    AvroSchemaType.Null => AvroValue.Null,
                    AvroSchemaType.Boolean => _random.Next(2) == 1,
                    AvroSchemaType.Int => Magnitude() switch { 0 => _random.Next(-64, 64), 1 => _random.Next(-100_000, 100_000), _ => _random.Next(int.MinValue, int.MaxValue) },
                    AvroSchemaType.Long => Magnitude() switch { 0 => (long)_random.Next(-64, 64), 1 => _random.Next(), _ => _random.NextInt64(long.MinValue, long.MaxValue) },
                    AvroSchemaType.Float => (float)((_random.NextDouble() * 2e6) - 1e6),
                    AvroSchemaType.Double => (_random.NextDouble() * 2e12) - 1e12,
                    AvroSchemaType.Bytes => Bytes(_random.Next(0, 40)),
                    _ => Text(_random.Next(0, 30)),
                };
        }
    }

    private AvroValue CreateUnion(UnionSchema union, int depth)
    {
        if (union.Branches.Count == 0)
        {
            throw new UninhabitedException();
        }

        // Deep in the tree, prefer branches that end the recursion.
        var candidates = depth > MaxDepth
            ? union.Branches.Where(b => b is not RecordSchema).ToList()
            : [.. union.Branches];
        if (candidates.Count == 0)
        {
            candidates = [.. union.Branches];
        }

        return Create(candidates[_random.Next(candidates.Count)], depth + 1);
    }

    private int Magnitude() => _random.Next(3);

    private byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        _random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Valid UTF-16 text (no lone surrogates) mixing ASCII, accented letters, CJK and emoji.</summary>
    private string Text(int length)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            switch (_random.Next(10))
            {
                case 0:
                    builder.Append('é');
                    break;
                case 1:
                    builder.Append('日');
                    break;
                case 2:
                    builder.Append("🎉");
                    break;
                default:
                    builder.Append((char)('a' + _random.Next(26)));
                    break;
            }
        }

        return builder.ToString();
    }

    private sealed class UninhabitedException : Exception
    {
        public UninhabitedException()
        {
        }

        public UninhabitedException(string message)
            : base(message)
        {
        }

        public UninhabitedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
