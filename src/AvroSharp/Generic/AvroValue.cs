using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

/// <summary>
/// Any Avro value in the generic data model, without boxing: primitives are stored inline, and strings, bytes,
/// records, enums, arrays, maps and fixed values by reference.
/// </summary>
/// <remarks>
/// <para>
/// The struct is 16 bytes. Primitive values are kept in a 64-bit field and identified by a shared marker object;
/// for every other kind, the reference itself identifies the kind. An enum stores its schema and ordinal, so
/// enum values do not allocate.
/// </para>
/// <para>
/// <see cref="AvroLogicalType">Logical types</see> are represented by their underlying Avro type: a <c>date</c> is an
/// <see cref="AvroValueKind.Int"/>, a <c>timestamp-micros</c> a <see cref="AvroValueKind.Long"/>, a <c>decimal</c> its
/// <see cref="AvroValueKind.Bytes"/>.
/// </para>
/// <para>
/// Arrays are held as <see cref="IReadOnlyList{T}"/> and maps as <see cref="IReadOnlyDictionary{TKey, TValue}"/>;
/// the reader creates <see cref="List{T}"/> and <see cref="Dictionary{TKey, TValue}"/> instances.
/// </para>
/// <para>
/// Arrays of <c>boolean</c>, <c>int</c>, <c>long</c>, <c>float</c> and <c>double</c> items are held as the primitive
/// values themselves: the reader creates them this way, and <see cref="FromInt64Array"/> and the other typed
/// factories wrap existing memory. <see cref="AsArray"/> still returns their items as values, and
/// <see cref="TryGetInt64Array"/> and the other typed accessors return the memory without a copy.
/// </para>
/// <para>
/// Factories and accessors pair by name: <c>From…</c> creates what <c>As…</c> returns. Primitives are named after their
/// .NET type (<see cref="FromInt32"/> and <see cref="AsInt32"/>, <c>Int64</c>, <c>Single</c>, <c>Double</c>, <c>Boolean</c>,
/// <c>String</c>), and the other kinds after their Avro type (<see cref="FromBytes"/>, <c>Record</c>, <c>Fixed</c>,
/// <c>Array</c>, <c>Map</c>, <c>Enum</c>).
/// </para>
/// </remarks>
/// <seealso cref="GenericRecord"/>
/// <seealso cref="AvroValueKind"/>
/// <seealso cref="GenericDatumReader"/>
/// <seealso cref="GenericDatumWriter"/>
public readonly struct AvroValue : IEquatable<AvroValue>
{
    private readonly long _bits;
    private readonly object? _reference;

    private AvroValue(long bits, object? reference)
    {
        _bits = bits;
        _reference = reference;
    }

    /// <summary>Gets the Avro <c>null</c> value (the default value of this struct).</summary>
    public static AvroValue Null => default;

    /// <summary>Gets the kind of value.</summary>
    public AvroValueKind Kind => _reference switch
    {
        null => AvroValueKind.Null,
        PrimitiveMarker marker => marker.Kind,
        string => AvroValueKind.String,
        byte[] => AvroValueKind.Bytes,
        GenericRecord => AvroValueKind.Record,
        Schemas.EnumSchema => AvroValueKind.Enum,
        GenericFixed => AvroValueKind.Fixed,

        // The concrete types the reader creates first, then any other list or dictionary.
        PrimitiveArray or List<AvroValue> or AvroValue[] => AvroValueKind.Array,
        Dictionary<string, AvroValue> => AvroValueKind.Map,
        IReadOnlyDictionary<string, AvroValue> => AvroValueKind.Map,
        _ => AvroValueKind.Array,
    };

    /// <summary>Gets a value indicating whether this is the Avro <c>null</c> value.</summary>
    public bool IsNull => _reference is null;

    /// <summary>Gets the enum schema when this is an enum value; otherwise <see langword="null"/>.</summary>
    public EnumSchema? EnumSchema => _reference as EnumSchema;

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(bool value) => new(value ? 1 : 0, PrimitiveMarker.Boolean);

    /// <summary>Creates an <c>int</c> value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(int value) => new(value, PrimitiveMarker.Int);

    /// <summary>Creates a <c>long</c> value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(long value) => new(value, PrimitiveMarker.Long);

    /// <summary>Creates a <c>float</c> value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(float value) => new(new FloatBits { Single = value }.Int32, PrimitiveMarker.Float);

    /// <summary>Creates a <c>double</c> value.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(double value) => new(BitConverter.DoubleToInt64Bits(value), PrimitiveMarker.Double);

    /// <summary>Creates a <c>string</c> value, or <c>null</c> for a <see langword="null"/> string.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(string? value) => new(0, value);

    /// <summary>Creates a <c>bytes</c> value, or <c>null</c> for a <see langword="null"/> array. The array is not copied.</summary>
    /// <param name="value">The value.</param>
#pragma warning disable CA2225 // The named alternates are FromBytes, FromRecord and FromFixed, which pair with AsBytes, AsRecord and AsFixed.
    public static implicit operator AvroValue(byte[]? value) => new(0, value);

    /// <summary>Creates a record value, or <c>null</c>.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(GenericRecord? value) => new(0, value);

    /// <summary>Creates a fixed value, or <c>null</c>.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator AvroValue(GenericFixed? value) => new(0, value);
#pragma warning restore CA2225

    /// <summary>Compares two values structurally (see <see cref="Equals(AvroValue)"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    public static bool operator ==(AvroValue left, AvroValue right) => left.Equals(right);

    /// <summary>Compares two values structurally.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    public static bool operator !=(AvroValue left, AvroValue right) => !left.Equals(right);

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromBoolean(bool value) => value;

    /// <summary>Creates an <c>int</c> value.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromInt32(int value) => value;

    /// <summary>Creates a <c>long</c> value.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromInt64(long value) => value;

    /// <summary>Creates a <c>float</c> value.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromSingle(float value) => value;

    /// <summary>Creates a <c>double</c> value.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromDouble(double value) => value;

    /// <summary>Creates a <c>string</c> value, or <c>null</c>.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromString(string? value) => value;

    /// <summary>Creates a <c>bytes</c> value, or <c>null</c>. The array is not copied.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromBytes(byte[]? value) => value;

    /// <summary>Creates a record value, or <c>null</c>.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromRecord(GenericRecord? value) => value;

    /// <summary>Creates a fixed value, or <c>null</c>.</summary>
    /// <param name="value">The value.</param>
    public static AvroValue FromFixed(GenericFixed? value) => value;

    /// <summary>Creates an array value from a list (for example a <see cref="List{T}"/> or an array). The list is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromArray(IReadOnlyList<AvroValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(0, items);
    }

    /// <summary>Creates an array of <c>boolean</c> items, stored as they are. The memory is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromBooleanArray(ReadOnlyMemory<bool> items) => new(0, new BooleanArray(items));

    /// <summary>Creates an array of <c>int</c> items, stored as they are. The memory is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromInt32Array(ReadOnlyMemory<int> items) => new(0, new Int32Array(items));

    /// <summary>Creates an array of <c>long</c> items, stored as they are. The memory is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromInt64Array(ReadOnlyMemory<long> items) => new(0, new Int64Array(items));

    /// <summary>Creates an array of <c>float</c> items, stored as they are. The memory is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromSingleArray(ReadOnlyMemory<float> items) => new(0, new SingleArray(items));

    /// <summary>Creates an array of <c>double</c> items, stored as they are. The memory is not copied.</summary>
    /// <param name="items">The items.</param>
    public static AvroValue FromDoubleArray(ReadOnlyMemory<double> items) => new(0, new DoubleArray(items));

    /// <summary>Creates a map value from a dictionary (for example a <see cref="Dictionary{TKey, TValue}"/>). The dictionary is not copied.</summary>
    /// <param name="entries">The entries.</param>
    public static AvroValue FromMap(IReadOnlyDictionary<string, AvroValue> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new(0, entries);
    }

    /// <summary>Creates an enum value from its ordinal.</summary>
    /// <param name="schema">The enum schema.</param>
    /// <param name="ordinal">The zero-based ordinal of the symbol.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is not a symbol of the enum.</exception>
    public static AvroValue FromEnum(EnumSchema schema, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if ((uint)ordinal >= (uint)schema.Symbols.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"Enum '{schema.FullName}' has {schema.Symbols.Count} symbols.");
        }

        return new(ordinal, schema);
    }

    /// <summary>Creates an enum value from its symbol.</summary>
    /// <param name="schema">The enum schema.</param>
    /// <param name="symbol">The symbol (case-sensitive).</param>
    /// <exception cref="ArgumentException">The symbol is not defined by the enum.</exception>
    public static AvroValue FromEnum(EnumSchema schema, string symbol)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(symbol);
        return schema.TryGetOrdinal(symbol, out var ordinal)
            ? new(ordinal, schema)
            : throw new ArgumentException($"'{symbol}' is not a symbol of enum '{schema.FullName}'.", nameof(symbol));
    }

    /// <summary>Gets the boolean.</summary>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    public bool AsBoolean() => Kind == AvroValueKind.Boolean ? _bits != 0 : throw WrongKind(AvroValueKind.Boolean);

    /// <summary>Gets the <c>int</c>.</summary>
    /// <exception cref="InvalidOperationException">The value is not an <c>int</c>.</exception>
    public int AsInt32() => Kind == AvroValueKind.Int ? (int)_bits : throw WrongKind(AvroValueKind.Int);

    /// <summary>Gets the value as a <c>long</c>; an <c>int</c> is widened.</summary>
    /// <exception cref="InvalidOperationException">The value is neither an <c>int</c> nor a <c>long</c>.</exception>
    public long AsInt64() => Kind is AvroValueKind.Long or AvroValueKind.Int ? _bits : throw WrongKind(AvroValueKind.Long);

    /// <summary>Gets the <c>float</c>.</summary>
    /// <exception cref="InvalidOperationException">The value is not a <c>float</c>.</exception>
    public float AsSingle() => Kind == AvroValueKind.Float ? new FloatBits { Int32 = (int)_bits }.Single : throw WrongKind(AvroValueKind.Float);

    /// <summary>Gets the value as a <c>double</c>; a <c>float</c> is widened.</summary>
    /// <exception cref="InvalidOperationException">The value is neither a <c>float</c> nor a <c>double</c>.</exception>
    public double AsDouble() => Kind switch
    {
        AvroValueKind.Double => BitConverter.Int64BitsToDouble(_bits),
        AvroValueKind.Float => new FloatBits { Int32 = (int)_bits }.Single,
        _ => throw WrongKind(AvroValueKind.Double),
    };

    /// <summary>Gets the string.</summary>
    /// <exception cref="InvalidOperationException">The value is not a string.</exception>
    public string AsString() => _reference as string ?? throw WrongKind(AvroValueKind.String);

    /// <summary>Gets the bytes (not a copy).</summary>
    /// <exception cref="InvalidOperationException">The value is not <c>bytes</c>.</exception>
    public byte[] AsBytes() => _reference as byte[] ?? throw WrongKind(AvroValueKind.Bytes);

    /// <summary>Gets the record.</summary>
    /// <exception cref="InvalidOperationException">The value is not a record.</exception>
    public GenericRecord AsRecord() => _reference as GenericRecord ?? throw WrongKind(AvroValueKind.Record);

    /// <summary>Gets the fixed value.</summary>
    /// <exception cref="InvalidOperationException">The value is not a fixed value.</exception>
    public GenericFixed AsFixed() => _reference as GenericFixed ?? throw WrongKind(AvroValueKind.Fixed);

    /// <summary>Gets the array items.</summary>
    /// <exception cref="InvalidOperationException">The value is not an array.</exception>
    public IReadOnlyList<AvroValue> AsArray() =>
        Kind == AvroValueKind.Array ? (IReadOnlyList<AvroValue>)_reference! : throw WrongKind(AvroValueKind.Array);

    /// <summary>Gets the items of an array stored as <c>boolean</c> values, without a copy.</summary>
    /// <param name="items">The items, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an array read from <c>boolean</c> items or made by <see cref="FromBooleanArray"/>; <see langword="false"/> for any other value, including an array of boolean values held as a list.</returns>
    public bool TryGetBooleanArray(out ReadOnlyMemory<bool> items) => TryGetItems(out items);

    /// <summary>Gets the items of an array stored as <c>int</c> values, without a copy.</summary>
    /// <param name="items">The items, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an array read from <c>int</c> items or made by <see cref="FromInt32Array"/>; <see langword="false"/> for any other value, including an array of int values held as a list.</returns>
    public bool TryGetInt32Array(out ReadOnlyMemory<int> items) => TryGetItems(out items);

    /// <summary>Gets the items of an array stored as <c>long</c> values, without a copy.</summary>
    /// <param name="items">The items, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an array read from <c>long</c> items or made by <see cref="FromInt64Array"/>; <see langword="false"/> for any other value, including an array of long values held as a list.</returns>
    public bool TryGetInt64Array(out ReadOnlyMemory<long> items) => TryGetItems(out items);

    /// <summary>Gets the items of an array stored as <c>float</c> values, without a copy.</summary>
    /// <param name="items">The items, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an array read from <c>float</c> items or made by <see cref="FromSingleArray"/>; <see langword="false"/> for any other value, including an array of float values held as a list.</returns>
    public bool TryGetSingleArray(out ReadOnlyMemory<float> items) => TryGetItems(out items);

    /// <summary>Gets the items of an array stored as <c>double</c> values, without a copy.</summary>
    /// <param name="items">The items, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an array read from <c>double</c> items or made by <see cref="FromDoubleArray"/>; <see langword="false"/> for any other value, including an array of double values held as a list.</returns>
    public bool TryGetDoubleArray(out ReadOnlyMemory<double> items) => TryGetItems(out items);

    /// <summary>Gets the map entries.</summary>
    /// <exception cref="InvalidOperationException">The value is not a map.</exception>
    public IReadOnlyDictionary<string, AvroValue> AsMap() =>
        _reference as IReadOnlyDictionary<string, AvroValue> ?? throw WrongKind(AvroValueKind.Map);

    /// <summary>Gets the enum ordinal.</summary>
    /// <exception cref="InvalidOperationException">The value is not an enum.</exception>
    public int AsEnumOrdinal() => _reference is EnumSchema ? (int)_bits : throw WrongKind(AvroValueKind.Enum);

    /// <summary>Gets the enum symbol.</summary>
    /// <exception cref="InvalidOperationException">The value is not an enum.</exception>
    public string AsEnumSymbol() => _reference is EnumSchema schema ? schema.Symbols[(int)_bits] : throw WrongKind(AvroValueKind.Enum);

    /// <summary>
    /// Compares structurally: same kind and equal contents. Floating-point values compare by bit pattern (so NaN
    /// equals NaN), byte arrays and fixed values by content, arrays in order, maps by key, records by schema name
    /// and field values, enums by schema name and ordinal.
    /// </summary>
    /// <param name="other">The other value.</param>
    public bool Equals(AvroValue other)
    {
        var kind = Kind;
        if (kind != other.Kind)
        {
            return false;
        }

        return kind switch
        {
            AvroValueKind.Null => true,
            AvroValueKind.Boolean or AvroValueKind.Int or AvroValueKind.Long or AvroValueKind.Float or AvroValueKind.Double => _bits == other._bits,
            AvroValueKind.String => string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
            AvroValueKind.Bytes => ((byte[])_reference!).AsSpan().SequenceEqual((byte[])other._reference!),
            AvroValueKind.Enum => _bits == other._bits && ((EnumSchema)_reference!).Name == ((EnumSchema)other._reference!).Name,
            AvroValueKind.Array => ArraysEqual(other),
            AvroValueKind.Map => MapsEqual(AsMap(), other.AsMap()),
            _ => _reference!.Equals(other._reference),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AvroValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Kind switch
    {
        AvroValueKind.Null => 0,
        AvroValueKind.String => StringComparer.Ordinal.GetHashCode((string)_reference!),
        AvroValueKind.Bytes => ((byte[])_reference!).Length,
        AvroValueKind.Array => AsArray().Count,
        AvroValueKind.Map => AsMap().Count,
        AvroValueKind.Record or AvroValueKind.Fixed => _reference!.GetHashCode(),
        _ => _bits.GetHashCode(),
    };

    /// <summary>Returns the value boxed as a CLR object: <see langword="null"/>, a primitive, or the referenced object.</summary>
    public object? ToObject() => Kind switch
    {
        AvroValueKind.Null => null,
        AvroValueKind.Boolean => AsBoolean(),
        AvroValueKind.Int => AsInt32(),
        AvroValueKind.Long => AsInt64(),
        AvroValueKind.Float => AsSingle(),
        AvroValueKind.Double => AsDouble(),
        AvroValueKind.Enum => AsEnumSymbol(),
        _ => _reference,
    };

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        AvroValueKind.Null => "null",
        AvroValueKind.Boolean => AsBoolean() ? "true" : "false",
        AvroValueKind.Bytes => Convert.ToBase64String(AsBytes()),
        AvroValueKind.Array => "[" + string.Join(", ", AsArray()) + "]",
        AvroValueKind.Map => "{" + string.Join(", ", AsMap().Select(e => e.Key + ": " + e.Value)) + "}",
        _ => Convert.ToString(ToObject(), CultureInfo.InvariantCulture) ?? string.Empty,
    };

    internal long Bits => _bits;

    // Kind tests for the writer's hot path: one reference comparison each, instead of the type checks in Kind.
    internal bool IsBoolean => ReferenceEquals(_reference, PrimitiveMarker.Boolean);

    internal bool IsInt => ReferenceEquals(_reference, PrimitiveMarker.Int);

    internal bool IsLong => ReferenceEquals(_reference, PrimitiveMarker.Long);

    internal bool IsFloat => ReferenceEquals(_reference, PrimitiveMarker.Float);

    internal bool IsDouble => ReferenceEquals(_reference, PrimitiveMarker.Double);

    internal float SingleUnchecked => new FloatBits { Int32 = (int)_bits }.Single;

    internal double DoubleUnchecked => BitConverter.Int64BitsToDouble(_bits);

    internal object? Reference => _reference;

    internal static AvroValue FromEnumUnchecked(EnumSchema schema, int ordinal) => new(ordinal, schema);

    private bool TryGetItems<T>(out ReadOnlyMemory<T> items)
        where T : unmanaged
    {
        if (_reference is PrimitiveArray<T> array)
        {
            items = array.Items;
            return true;
        }

        items = default;
        return false;
    }

    // Two typed arrays of the same element type compare as memory; anything else item by item.
    private bool ArraysEqual(in AvroValue other) =>
        _reference is PrimitiveArray left && other._reference is PrimitiveArray right && left.ItemsEqual(right) is { } equal
            ? equal
            : AsArray().SequenceEqual(other.AsArray());

    private static bool MapsEqual(IReadOnlyDictionary<string, AvroValue> left, IReadOnlyDictionary<string, AvroValue> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var value) || !entry.Value.Equals(value))
            {
                return false;
            }
        }

        return true;
    }

    private InvalidOperationException WrongKind(AvroValueKind expected) =>
        new($"The value is {Kind}, not {expected}.");

    /// <summary>Reinterprets a float as its bit pattern without unsafe code or allocation, on every target.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)]
        public float Single;

        [FieldOffset(0)]
        public int Int32;
    }

    /// <summary>Identifies the kind of an inline primitive value.</summary>
    private sealed class PrimitiveMarker
    {
        private PrimitiveMarker(AvroValueKind kind) => Kind = kind;

        public static PrimitiveMarker Boolean { get; } = new(AvroValueKind.Boolean);

        public static PrimitiveMarker Int { get; } = new(AvroValueKind.Int);

        public static PrimitiveMarker Long { get; } = new(AvroValueKind.Long);

        public static PrimitiveMarker Float { get; } = new(AvroValueKind.Float);

        public static PrimitiveMarker Double { get; } = new(AvroValueKind.Double);

        public AvroValueKind Kind { get; }
    }
}
