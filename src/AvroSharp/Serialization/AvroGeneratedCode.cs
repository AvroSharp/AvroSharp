using System;
using System.ComponentModel;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Serialization;

/// <summary>Writes one value.</summary>
/// <typeparam name="T">The type written.</typeparam>
/// <param name="writer">The destination.</param>
/// <param name="value">The value.</param>
public delegate void AvroWriteAction<in T>(ref AvroWriter writer, T value);

/// <summary>Reads one value.</summary>
/// <typeparam name="T">The type read.</typeparam>
/// <param name="reader">The source.</param>
public delegate T AvroReadFunc<out T>(ref AvroReader reader);

/// <summary>
/// Support for the serializers that the AvroSharp source generator emits. The generated code calls these members;
/// they are not meant to be called directly and may change between versions together with the generator.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class AvroGeneratedCode
{
    /// <summary>The deepest nesting of records a generated serializer writes or reads, as in the generic model.</summary>
    public const int MaxDepth = 128;

    /// <summary>
    /// The most zero-size array items (for example <c>null</c>s or empty records) one value may declare, across all of
    /// its arrays, as in the generic model; input size cannot bound them.
    /// </summary>
    public const int MaxZeroSizeItems = 1 << 16;

    // Items pre-allocated for an array or map before any are read; larger blocks grow as items arrive.
    private const int PreallocationLimit = 1024;

    // The largest element count of a .NET array (Array.MaxLength on net6+).
    private const int MaxCollectionCount = 0x7FFFFFC7;

    /// <summary>Writes a value to a new array.</summary>
    /// <typeparam name="T">The generated type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="write">The generated write method.</param>
    public static byte[] SerializeToArray<T>(T value, AvroWriteAction<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var output = new PooledBufferWriter();
        var writer = new AvroWriter(output);
        write(ref writer, value);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// Starts reading one value: resets the count of zero-size items that <see cref="ReadBlockItemCount"/> limits.
    /// Generated <c>Read</c> methods call it, so a reader reused for a sequence of values (a container block) gives each
    /// value the whole budget.
    /// </summary>
    /// <param name="reader">The source.</param>
    public static void BeginRead(ref AvroReader reader) => reader.ZeroSizeItems = 0;

    /// <summary>
    /// Reads the item count of the next array or map block (0 at the end) and checks it before anything is
    /// allocated: items of at least <paramref name="minimumItemSize"/> bytes must fit in the remaining input, zero-size
    /// items are limited to <see cref="MaxZeroSizeItems"/> in the whole value being read (nested arrays share the
    /// limit), and no collection may exceed the largest .NET array.
    /// </summary>
    /// <param name="reader">The source.</param>
    /// <param name="minimumItemSize">The smallest encoded size of one item (a map entry includes its key).</param>
    /// <param name="itemsSoFar">The items already read into this collection.</param>
    /// <exception cref="AvroDataException">The count cannot be satisfied by the input.</exception>
    public static int ReadBlockItemCount(ref AvroReader reader, int minimumItemSize, int itemsSoFar)
    {
        var count = reader.ReadBlockCount(out _);
        if (count == 0)
        {
            return 0;
        }

        // No collection may exceed the largest .NET array; below that bound, count * minimumItemSize cannot overflow a
        // long, so the input check multiplies instead of dividing (a 64-bit division costs 10 to 90 cycles per block).
        if (count > MaxCollectionCount - itemsSoFar)
        {
            throw new AvroDataException($"The collection would hold more than {MaxCollectionCount} items.");
        }

        if (minimumItemSize > 0)
        {
            if (count * minimumItemSize > reader.BytesRemaining)
            {
                throw new AvroDataException($"Block count {count} is larger than the remaining input can hold.");
            }
        }
        else
        {
            // Charged to the whole value, not to this collection: an array of records that each hold an array of
            // nulls would otherwise declare MaxZeroSizeItems per record, from a few bytes each.
            if (count > MaxZeroSizeItems - reader.ZeroSizeItems)
            {
                throw new AvroDataException($"The value declares more than {MaxZeroSizeItems} zero-size items.");
            }

            reader.ZeroSizeItems += count;
        }

        return (int)count;
    }

    /// <summary>Gets the capacity to reserve for a collection's first block: its count, up to a limit.</summary>
    /// <param name="count">The first block's item count.</param>
    public static int InitialCapacity(int count) => Math.Min(count, PreallocationLimit);

    /// <summary>Reads an enum ordinal and checks that it names a symbol.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="symbolCount">The number of symbols.</param>
    /// <param name="enumName">The enum's full name, for the error message.</param>
    public static int ReadEnumOrdinal(ref AvroReader reader, int symbolCount, string enumName)
    {
        var ordinal = reader.ReadEnum();
        return (uint)ordinal < (uint)symbolCount
            ? ordinal
            : throw new AvroDataException($"Enum ordinal {ordinal} is out of range for '{enumName}' ({symbolCount} symbols).");
    }

    /// <summary>Creates the error for a union branch index that the union does not have.</summary>
    /// <param name="index">The index read.</param>
    /// <param name="branchCount">The number of branches.</param>
    public static AvroDataException InvalidUnionIndex(int index, int branchCount) =>
        new($"Union branch index {index} is out of range ({branchCount} branches).");

    /// <summary>Creates the error for a value that no branch of a union accepts.</summary>
    /// <param name="value">The value.</param>
    /// <param name="field">The field, as <c>Record.field</c>.</param>
    public static AvroException UnionValueMismatch(object? value, string field) =>
        new($"Field '{field}': a value of type {value?.GetType().FullName ?? "null"} matches no branch of its union.");

    /// <summary>Creates the error for a <see langword="null"/> in a field whose schema does not allow null.</summary>
    /// <param name="field">The field, as <c>Record.field</c>.</param>
    public static AvroException NullValue(string field) =>
        new($"Field '{field}' is null, but its schema does not allow null.");

    /// <summary>Creates the error for a field position that a record does not have.</summary>
    /// <param name="fieldPos">The position.</param>
    /// <param name="fieldCount">The record's number of fields.</param>
    /// <param name="recordName">The record's full name.</param>
    public static AvroException InvalidFieldPosition(int fieldPos, int fieldCount, string recordName) =>
        new($"Record '{recordName}' has no field at position {fieldPos} ({fieldCount} fields).");

    /// <summary>Creates the error for a value whose type does not match the field it is put into.</summary>
    /// <param name="value">The value.</param>
    /// <param name="field">The field, as <c>Record.field</c>.</param>
    /// <param name="expectedType">The field's C# type.</param>
    public static AvroException PutTypeMismatch(object? value, string field, string expectedType) =>
        new($"Field '{field}' holds {expectedType}; a {(value is null ? "null" : "value of type " + value.GetType().FullName)} cannot be put into it.");

    /// <summary>Creates the error for records nested deeper than <see cref="MaxDepth"/> while writing.</summary>
    public static AvroException WriteTooDeep() =>
        new($"Records are nested more than {MaxDepth} levels deep; a record may contain itself.");

    /// <summary>Creates the error for records nested deeper than <see cref="MaxDepth"/> while reading.</summary>
    public static AvroDataException ReadTooDeep() =>
        new($"Records are nested more than {MaxDepth} levels deep.");

    /// <summary>
    /// Gets whether data written with <paramref name="writerSchema"/> can be read as <paramref name="readerSchema"/>
    /// without resolution: the same instance, or the same Parsing Canonical Form (identical encoding).
    /// </summary>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <param name="readerSchema">The generated type's schema.</param>
    public static bool IsSameSchema(AvroSchema writerSchema, AvroSchema readerSchema)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        return writerSchema.HasSameCanonicalForm(readerSchema);
    }

    /// <summary>
    /// Reads one value written with <paramref name="writerSchema"/> and returns it in <paramref name="readerSchema"/>'s
    /// encoding, resolved as the specification says, for the generated reader to read. No generic values are created.
    /// </summary>
    /// <remarks>
    /// The result is a per-thread buffer that the next call on the thread overwrites: read it before calling again.
    /// </remarks>
    /// <param name="reader">The source.</param>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <param name="readerSchema">The generated type's schema.</param>
    public static ReadOnlySpan<byte> ResolveToReaderEncoding(ref AvroReader reader, AvroSchema writerSchema, AvroSchema readerSchema)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        var buffer = ResolvedBuffer();
        GenericDatumReader.GetTranscoder(writerSchema, readerSchema).Transcode(ref reader, buffer);
        return buffer.WrittenSpan;
    }

    /// <summary>
    /// Stores <paramref name="schema"/> in <paramref name="field"/> unless another thread stored one first, and returns
    /// the stored one, so a generated type's <c>Schema</c> is one instance even when first read on several threads at
    /// once (plans are cached per instance, and the same instance is the fast path for resolution).
    /// </summary>
    /// <param name="field">The generated type's schema field.</param>
    /// <param name="schema">The schema just parsed.</param>
    public static AvroSchema PublishSchema(ref AvroSchema? field, AvroSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return System.Threading.Interlocked.CompareExchange(ref field, schema, null) ?? schema;
    }

    /// <summary>
    /// Gets the plan for reading a generated record from data of another version of its schema, or <see langword="null"/>
    /// when <paramref name="writerSchema"/> is not a record of the same name (then use <see cref="ResolveToReaderEncoding"/>).
    /// Plans are built once per pair of schemas.
    /// </summary>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <param name="readerSchema">The generated type's schema.</param>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved.</exception>
    public static AvroRecordPlan? GetRecordPlan(AvroSchema writerSchema, AvroSchema readerSchema)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        return GenericDatumReader.GetRecordPlan(writerSchema, readerSchema);
    }

    // The per-thread buffer that resolved values are written to, emptied.
    internal static PooledBufferWriter ResolvedBuffer()
    {
        var buffer = s_resolved;
        if (buffer is null || buffer.Capacity > MaxRetainedResolvedBuffer)
        {
            buffer?.Dispose();
            buffer = s_resolved = new PooledBufferWriter(1024);
        }

        buffer.Clear();
        return buffer;
    }

    // A buffer grown past this by one large value is replaced on the next call instead of being kept by the thread.
    private const int MaxRetainedResolvedBuffer = 1024 * 1024;

    [ThreadStatic]
    private static PooledBufferWriter? s_resolved;

    /// <summary>Checks the length of a fixed value's bytes.</summary>
    /// <param name="value">The bytes.</param>
    /// <param name="size">The fixed type's size.</param>
    /// <param name="typeName">The fixed type's full name, for the error message.</param>
    public static byte[] CheckFixedSize(byte[] value, int size, string typeName)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == size
            ? value
            : throw new ArgumentException($"'{typeName}' holds exactly {size} bytes, not {value.Length}.", nameof(value));
    }
}
