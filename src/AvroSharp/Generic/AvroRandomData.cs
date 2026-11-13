using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Random values of a schema, for property tests and fuzz corpora, as Apache Avro Java's <c>RandomData</c> makes them.
/// The same seed and options give the same values, on every target framework.
/// </summary>
/// <remarks>
/// <para>
/// Every value can be written with the schema, and logical values are valid: decimals fit their precision, times are
/// within a day, UUIDs are well formed, and dates and timestamps stay in .NET's range unless
/// <see cref="AvroRandomDataOptions.LogicalValuesInDotNetRange"/> is off. Numbers mix small and large magnitudes;
/// floating-point values are finite. Strings mix ASCII, accented letters, CJK and emoji (characters outside the Basic
/// Multilingual Plane).
/// </para>
/// <para>
/// A recursive schema ends at <see cref="AvroRandomDataOptions.MaxDepth"/>. Once a value holds 10,000 values, the
/// arrays and maps it starts after that are empty and its unions end as soon as they can, so a schema whose records
/// branch out stays small. A schema with no finite value, such as a record that contains
/// itself outside a union, array or map, is an error.
/// </para>
/// <para>An instance is not thread-safe.</para>
/// </remarks>
/// <example>
/// <code>
/// foreach (var value in AvroRandomData.Generate(schema, seed: 42).Take(100))
/// {
///     ...
/// }
/// </code>
/// </example>
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Test data, not secrets: the values must repeat for a seed, which a secure generator can't do.")]
public sealed class AvroRandomData
{
    private const int Infinite = int.MaxValue;
    private const int MaxMapKeyLength = 8;

    // Values one generated value holds before the generator ends it as below MaxDepth. Several recursive fields per
    // record, or arrays of records, would otherwise grow a value exponentially before it reaches MaxDepth.
    private const int MaxValuesPerValue = 10_000;

    // DateTime's range: 0001-01-01T00:00:00 to 9999-12-31T23:59:59.999.
    private const long MinMilliseconds = -62_135_596_800_000;
    private const long MaxMilliseconds = 253_402_300_799_999;
    private const int MinDays = -719_162;
    private const int MaxDays = 2_932_896;

    private readonly Random _random;
    private readonly AvroRandomDataOptions _options;

    // The fewest records a value of each record schema nests, by reference (Infinite: it has no finite value).
    private readonly Dictionary<RecordSchema, int> _heights = [];

    // The values the current top-level value holds so far.
    private int _created;

    /// <summary>Initializes a generator.</summary>
    /// <param name="seed">The seed: the same seed gives the same values.</param>
    /// <param name="options">The shape of the values, or <see langword="null"/> for <see cref="AvroRandomDataOptions.Default"/>.</param>
    public AvroRandomData(int seed, AvroRandomDataOptions? options = null)
    {
        // Random's seeded algorithm is the same on .NET Framework and every .NET; only its basic methods are used.
        _random = new Random(seed);
        _options = options ?? AvroRandomDataOptions.Default;
    }

    /// <summary>Generates values of a schema, without end; take as many as needed.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="seed">The seed: the same seed gives the same values.</param>
    /// <param name="options">The shape of the values, or <see langword="null"/> for <see cref="AvroRandomDataOptions.Default"/>.</param>
    /// <exception cref="AvroException">The schema has no finite value.</exception>
    public static IEnumerable<AvroValue> Generate(AvroSchema schema, int seed, AvroRandomDataOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var generator = new AvroRandomData(seed, options);
        generator.EnsureFinite(schema);
        return generator.Values(schema);
    }

    /// <summary>Generates the next value of a schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <exception cref="AvroException">The schema has no finite value.</exception>
    public AvroValue Next(AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        EnsureFinite(schema);
        _created = 0;
        return Create(schema, 0);
    }

    private IEnumerable<AvroValue> Values(AvroSchema schema)
    {
        while (true)
        {
            _created = 0;
            yield return Create(schema, 0);
        }
    }

    private void EnsureFinite(AvroSchema schema)
    {
        if (Height(schema) == Infinite)
        {
            throw new AvroException($"The schema {schema.CanonicalForm} has no finite value: a record contains itself outside a union, array or map.");
        }
    }

    // depth: the number of records around the value.
    private AvroValue Create(AvroSchema schema, int depth)
    {
        _created++;
        switch (schema)
        {
            case RecordSchema record:
                var value = new GenericRecord(record);
                foreach (var field in record.Fields)
                {
                    value[field.Position] = Create(field.Schema, depth + 1);
                }

                return value;
            case EnumSchema enumSchema:
                return AvroValue.FromEnum(enumSchema, _random.Next(enumSchema.Symbols.Count));
            case FixedSchema fixedSchema:
                return new GenericFixed(fixedSchema, fixedSchema.LogicalType is DecimalLogicalType fixedDecimal
                    ? DecimalBytes(fixedDecimal, fixedSchema.Size)
                    : Bytes(fixedSchema.Size));
            case ArraySchema array:
                var items = new List<AvroValue>();
                for (var i = CollectionLength(array.Items, depth); i > 0; i--)
                {
                    items.Add(Create(array.Items, depth));
                }

                return AvroValue.FromArray(items);
            case MapSchema map:
                var entries = new Dictionary<string, AvroValue>(StringComparer.Ordinal);
                for (var i = CollectionLength(map.Values, depth); i > 0; i--)
                {
                    entries[Text(_random.Next(Math.Min(MaxMapKeyLength, _options.MaxStringLength) + 1))] = Create(map.Values, depth);
                }

                return AvroValue.FromMap(entries);
            case UnionSchema union:
                return Create(Branch(union, depth), depth);
            case { LogicalType: { } logical } when LogicalValue(schema.Type, logical) is { } logicalValue:
                return logicalValue;
            default:
                return Primitive(schema.Type);
        }
    }

    // Numbers mix small and large magnitudes.
    private AvroValue Primitive(AvroSchemaType type) => type switch
    {
        AvroSchemaType.Null => AvroValue.Null,
        AvroSchemaType.Boolean => _random.Next(2) == 1,
        AvroSchemaType.Int => _random.Next(3) switch
        {
            0 => _random.Next(-64, 64),
            1 => _random.Next(-100_000, 100_000),
            _ => (int)NextInt64(int.MinValue, (long)int.MaxValue + 1),
        },
        AvroSchemaType.Long => _random.Next(3) switch
        {
            0 => (long)_random.Next(-64, 64),
            1 => (long)_random.Next(),
            _ => NextInt64(long.MinValue, long.MaxValue),
        },
        AvroSchemaType.Float => (float)((_random.NextDouble() * 2e6) - 1e6),
        AvroSchemaType.Double => (_random.NextDouble() * 2e12) - 1e12,
        AvroSchemaType.Bytes => Bytes(_random.Next(_options.MaxStringLength + 1)),
        _ => Text(_random.Next(_options.MaxStringLength + 1)),
    };

    // Empty when the value is ending, and when the items have no finite value.
    private int CollectionLength(AvroSchema items, int depth) =>
        Ending(depth) || Height(items) == Infinite ? 0 : _random.Next(_options.MaxCollectionLength + 1);

    // Below MaxDepth, or past the size limit: collections are empty and unions take the branch that ends soonest.
    private bool Ending(int depth) => depth >= _options.MaxDepth || _created >= MaxValuesPerValue;

    /// <summary>
    /// A branch with a finite value. Below <see cref="AvroRandomDataOptions.MaxDepth"/>, one of the branches that nest
    /// the fewest records, so the recursion ends: each record on the way has a branch shallower than itself.
    /// </summary>
    private AvroSchema Branch(UnionSchema union, int depth)
    {
        var branches = union.Branches;
        var deep = Ending(depth);
        var limit = Infinite - 1;
        if (deep)
        {
            limit = Infinite;
            foreach (var branch in branches)
            {
                limit = Math.Min(limit, Height(branch));
            }
        }

        var candidates = new List<AvroSchema>(branches.Count);
        AvroSchema? nullBranch = null;
        foreach (var branch in branches)
        {
            if (Height(branch) > limit)
            {
                continue;
            }

            if (branch.Type == AvroSchemaType.Null)
            {
                nullBranch = branch;
            }
            else
            {
                candidates.Add(branch);
            }
        }

        if (nullBranch is not null)
        {
            if (_options.NullBranchProbability is not { } probability)
            {
                candidates.Add(nullBranch);
            }
            else if (candidates.Count == 0 || _random.NextDouble() < probability)
            {
                return nullBranch;
            }
        }

        return candidates[_random.Next(candidates.Count)];
    }

    /// <summary>The fewest records a value of the schema nests: 0 for every type but a record, <see cref="Infinite"/> for none.</summary>
    private int Height(AvroSchema schema)
    {
        switch (schema)
        {
            case RecordSchema record:
                if (!_heights.TryGetValue(record, out var height))
                {
                    ComputeHeights(record);
                    height = _heights[record];
                }

                return height;
            case UnionSchema union:
                var lowest = Infinite;
                foreach (var branch in union.Branches)
                {
                    lowest = Math.Min(lowest, Height(branch));
                }

                return lowest;
            case EnumSchema { Symbols.Count: 0 }:
                return Infinite;
            default:
                // Primitives, enums and fixed values, and arrays and maps, which can be empty.
                return 0;
        }
    }

    /// <summary>
    /// The heights of every record reachable from <paramref name="root"/>, as a fixed point: each starts as Infinite and
    /// only falls, so records that refer to each other settle once none of them changes.
    /// </summary>
    private void ComputeHeights(RecordSchema root)
    {
        var records = new List<RecordSchema>();
        Collect(root, records);
        foreach (var record in records)
        {
            _heights[record] = Infinite;
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var record in records)
            {
                var height = 0;
                foreach (var field in record.Fields)
                {
                    height = Math.Max(height, Height(field.Schema));
                }

                height = height == Infinite ? Infinite : height + 1;
                if (height < _heights[record])
                {
                    _heights[record] = height;
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private void Collect(AvroSchema schema, List<RecordSchema> records)
    {
        switch (schema)
        {
            case RecordSchema record when !_heights.ContainsKey(record) && !records.Contains(record):
                records.Add(record);
                foreach (var field in record.Fields)
                {
                    Collect(field.Schema, records);
                }

                break;
            case ArraySchema array:
                Collect(array.Items, records);
                break;
            case MapSchema map:
                Collect(map.Values, records);
                break;
            case UnionSchema union:
                foreach (var branch in union.Branches)
                {
                    Collect(branch, records);
                }

                break;
        }
    }

    /// <summary>A value of a logical type, or <see langword="null"/> when every value of the underlying type is valid.</summary>
    private AvroValue? LogicalValue(AvroSchemaType type, AvroLogicalType logical)
    {
        var inRange = _options.LogicalValuesInDotNetRange;
        return (logical.Kind, type) switch
        {
            (AvroLogicalTypeKind.Date, AvroSchemaType.Int) when inRange => _random.Next(MinDays, MaxDays + 1),
            (AvroLogicalTypeKind.TimeMillis, AvroSchemaType.Int) => _random.Next(0, 86_400_000),
            (AvroLogicalTypeKind.TimeMicros, AvroSchemaType.Long) => NextInt64(0, 86_400_000_000),
            (AvroLogicalTypeKind.TimestampMillis or AvroLogicalTypeKind.LocalTimestampMillis, AvroSchemaType.Long) when inRange =>
                NextInt64(MinMilliseconds, MaxMilliseconds + 1),
            (AvroLogicalTypeKind.TimestampMicros or AvroLogicalTypeKind.LocalTimestampMicros, AvroSchemaType.Long) when inRange =>
                NextInt64(MinMilliseconds * 1000, (MaxMilliseconds * 1000) + 1000),
            (AvroLogicalTypeKind.Uuid, AvroSchemaType.String) => new Guid(Bytes(16)).ToString("D"),
            (AvroLogicalTypeKind.Decimal, AvroSchemaType.Bytes) when logical is DecimalLogicalType bytesDecimal => DecimalBytes(bytesDecimal, size: -1),
            (AvroLogicalTypeKind.BigDecimal, AvroSchemaType.Bytes) => BigDecimalBytes(),
            _ => null,
        };
    }

    /// <summary>
    /// An unscaled decimal of at most the schema's precision in digits, as minimal two's-complement big-endian bytes, or
    /// sign-extended to <paramref name="size"/> bytes for a fixed type.
    /// </summary>
    private byte[] DecimalBytes(DecimalLogicalType logical, int size)
    {
        var unscaled = Unscaled(logical.Precision);
        var bytes = BigEndian(unscaled);
        if (size < 0 || bytes.Length == size)
        {
            return bytes;
        }

        var padded = new byte[size];
        padded.AsSpan().Fill(unscaled.Sign < 0 ? (byte)0xFF : (byte)0);
        bytes.CopyTo(padded, size - bytes.Length);
        return padded;
    }

    /// <summary><c>big-decimal</c> as Apache Avro Java encodes it: the unscaled value as Avro <c>bytes</c>, then the scale as an Avro <c>int</c>.</summary>
    private byte[] BigDecimalBytes()
    {
        var unscaled = BigEndian(Unscaled(20));
        var scale = _random.Next(0, 11);
        var output = new List<byte>(unscaled.Length + 6);
        AppendVarint(output, unscaled.Length);
        output.AddRange(unscaled);
        AppendVarint(output, scale);
        return [.. output];
    }

    private static void AppendVarint(List<byte> output, long value)
    {
        var zigzag = (ulong)((value << 1) ^ (value >> 63));
        while (zigzag >= 0x80)
        {
            output.Add((byte)(zigzag | 0x80));
            zigzag >>= 7;
        }

        output.Add((byte)zigzag);
    }

    private BigInteger Unscaled(int precision)
    {
        var digits = new StringBuilder();
        for (var i = _random.Next(1, precision + 1); i > 0; i--)
        {
            digits.Append((char)('0' + _random.Next(10)));
        }

        var unscaled = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        return _random.Next(2) == 1 ? -unscaled : unscaled;
    }

    // Minimal two's complement, big-endian.
    private static byte[] BigEndian(BigInteger value)
    {
        var bytes = value.ToByteArray();
        Array.Reverse(bytes);
        return bytes;
    }

    /// <summary>A uniform long in [<paramref name="min"/>, <paramref name="maxExclusive"/>), from Random's bytes, so it is the same on every target.</summary>
    private long NextInt64(long min, long maxExclusive)
    {
        var bytes = new byte[8];
        _random.NextBytes(bytes);
        var range = (ulong)(maxExclusive - min);
        return (long)((ulong)min + (BinaryPrimitives.ReadUInt64LittleEndian(bytes) % range));
    }

    private byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        _random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Valid UTF-16 text, at most <paramref name="length"/> code units: ASCII letters, an accented letter, CJK and emoji.</summary>
    private string Text(int length)
    {
        var builder = new StringBuilder(length);
        while (builder.Length < length)
        {
            switch (_random.Next(10))
            {
                case 0:
                    builder.Append('é');
                    break;
                case 1:
                    builder.Append('日');
                    break;
                case 2 when builder.Length + 2 <= length:
                    builder.Append("🎉");
                    break;
                default:
                    builder.Append((char)('a' + _random.Next(26)));
                    break;
            }
        }

        return builder.ToString();
    }
}
