using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Serialization;

/// <summary>
/// A type's Avro schema and its read and write functions, without reflection: from <see cref="AvroTypes"/>, for
/// code that has only the <see cref="Type"/> (<see cref="ReadObject(ref AvroReader)"/> and
/// <see cref="WriteObject(ref AvroWriter, object?)"/> box values).
/// </summary>
/// <seealso cref="AvroTypeInfo{T}"/>
public abstract class AvroTypeInfo
{
    private protected AvroTypeInfo()
    {
    }

    /// <summary>Gets the type.</summary>
    public abstract Type Type { get; }

    /// <summary>Gets the type's Avro schema.</summary>
    public abstract AvroSchema Schema { get; }

    /// <summary>Writes a value of <see cref="Type"/>.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/> and <see cref="Type"/> is a value type.</exception>
    /// <exception cref="InvalidCastException"><paramref name="value"/> is not a <see cref="Type"/>.</exception>
    public abstract void WriteObject(ref AvroWriter writer, object? value);

    /// <summary>Reads a value written with the type's schema.</summary>
    /// <param name="reader">The source.</param>
    public abstract object? ReadObject(ref AvroReader reader);

    /// <summary>Reads a value written with <paramref name="writerSchema"/>, another version of the type's schema.</summary>
    /// <param name="reader">The source.</param>
    /// <param name="writerSchema">The schema the data was written with.</param>
    public abstract object? ReadObject(ref AvroReader reader, AvroSchema writerSchema);
}

/// <summary>A type's Avro schema and its read and write functions.</summary>
/// <typeparam name="T">The type.</typeparam>
/// <seealso cref="AvroTypes"/>
public sealed class AvroTypeInfo<T> : AvroTypeInfo
{
    private readonly Func<AvroSchema> _schema;
    private readonly Func<AvroSchema, AvroReadFunc<T>> _readFor;
    private AvroSchema? _value;

    /// <summary>Initializes a new instance of the <see cref="AvroTypeInfo{T}"/> class.</summary>
    /// <param name="schema">Gets the schema, called once, when it is first needed.</param>
    /// <param name="write">Writes a value.</param>
    /// <param name="read">Reads a value written with the schema.</param>
    /// <param name="readFor">Returns the reader of data written with another schema, or <see langword="null"/> when the type has none (it then reads only its own schema).</param>
    public AvroTypeInfo(Func<AvroSchema> schema, AvroWriteAction<T> write, AvroReadFunc<T> read, Func<AvroSchema, AvroReadFunc<T>>? readFor = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(read);
        _schema = schema;
        Write = write;
        Read = read;
        _readFor = readFor ?? (writerSchema => writerSchema.HasSameCanonicalForm(Schema)
            ? Read
            : throw new AvroSchemaException($"{typeof(T)} reads data of its own schema only, not of {writerSchema.CanonicalForm}."));
    }

    /// <inheritdoc/>
    public override Type Type => typeof(T);

    /// <inheritdoc/>
    public override AvroSchema Schema => Volatile.Read(ref _value) ?? Publish();

    /// <summary>Gets the function that writes a value.</summary>
    public AvroWriteAction<T> Write { get; }

    /// <summary>Gets the function that reads a value written with <see cref="Schema"/>.</summary>
    public AvroReadFunc<T> Read { get; }

    /// <summary>Gets the function that reads values written with <paramref name="writerSchema"/>: <see cref="Read"/> when it has the type's own encoding.</summary>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <remarks>
    /// Schemas that can't be resolved fail here or when a value is read, depending on the type: a generated type
    /// resolves when it reads (and caches the plan); a primitive resolves here.
    /// </remarks>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved (for a primitive, or a type whose function resolves at once).</exception>
    public AvroReadFunc<T> ReadFor(AvroSchema writerSchema)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        return _readFor(writerSchema);
    }

    /// <inheritdoc/>
    public override void WriteObject(ref AvroWriter writer, object? value)
    {
        if (value is null && default(T) is not null)
        {
            throw new ArgumentNullException(nameof(value), $"{typeof(T)} is a value type, which can't be null.");
        }

        Write(ref writer, (T)value!);
    }

    /// <inheritdoc/>
    public override object? ReadObject(ref AvroReader reader) => Read(ref reader);

    /// <inheritdoc/>
    public override object? ReadObject(ref AvroReader reader, AvroSchema writerSchema) => ReadFor(writerSchema)(ref reader);

    private AvroSchema Publish()
    {
        var schema = _schema() ?? throw new InvalidOperationException($"The schema function of {typeof(T)} returned null.");
        return Interlocked.CompareExchange(ref _value, schema, null) ?? schema;
    }
}

/// <summary>
/// Finds a type's <see cref="AvroTypeInfo{T}"/> without reflection, so generic code and code that has only a
/// <see cref="Type"/> can read and write it on every target and under Native AOT. Generated types register
/// themselves when their assembly loads (on targets with module initializers, .NET 5 and later; elsewhere call
/// <see cref="Register{T}"/> with the type's <c>AvroTypeInfo</c>). <see langword="bool"/>, <see langword="int"/>,
/// <see langword="long"/>, <see langword="float"/>, <see langword="double"/>, <see langword="string"/> and
/// <c>byte[]</c> are registered with their primitive schemas.
/// </summary>
public static class AvroTypes
{
    private static readonly ConcurrentDictionary<Type, AvroTypeInfo> s_types = new();

    static AvroTypes()
    {
        Primitive<bool>(AvroSchemaType.Boolean, static (ref w, v) => w.WriteBoolean(v), static (ref r) => r.ReadBoolean());
        Primitive<int>(AvroSchemaType.Int, static (ref w, v) => w.WriteInt(v), static (ref r) => r.ReadInt());
        Primitive<long>(AvroSchemaType.Long, static (ref w, v) => w.WriteLong(v), static (ref r) => r.ReadLong());
        Primitive<float>(AvroSchemaType.Float, static (ref w, v) => w.WriteFloat(v), static (ref r) => r.ReadFloat());
        Primitive<double>(AvroSchemaType.Double, static (ref w, v) => w.WriteDouble(v), static (ref r) => r.ReadDouble());
        Primitive<string>(AvroSchemaType.String, static (ref w, v) => w.WriteString(v), static (ref r) => r.ReadString());
        Primitive<byte[]>(AvroSchemaType.Bytes, static (ref w, v) => w.WriteBytes(v), static (ref r) => r.ReadBytes());
    }

    /// <summary>Registers a type; generated code calls it. A type registered again keeps its first registration.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="info">The type's schema and functions.</param>
    /// <returns><see langword="true"/> when this registered the type; <see langword="false"/> when it was registered already.</returns>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static bool Register<T>(AvroTypeInfo<T> info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!s_types.TryAdd(typeof(T), info))
        {
            return false;
        }

        Cache<T>.Info = info;
        return true;
    }

    /// <summary>Gets a type's registration.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    public static AvroTypeInfo<T> Get<T>() =>
        TryGet<T>(out var info) ? info : throw new InvalidOperationException($"{typeof(T)} has no Avro serializers: it is not a generated type, or its assembly has not registered it (AvroTypes.Register).");

    /// <summary>Gets a type's registration, if it has one.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="info">The registration.</param>
    public static bool TryGet<T>([NotNullWhen(true)] out AvroTypeInfo<T>? info)
    {
        info = Cache<T>.Info ?? (s_types.TryGetValue(typeof(T), out var found) ? (AvroTypeInfo<T>)found : null);
        return info is not null;
    }

    /// <summary>Gets the registration of <paramref name="type"/>, if it has one.</summary>
    /// <param name="type">The type.</param>
    /// <param name="info">The registration.</param>
    public static bool TryGet(Type type, [NotNullWhen(true)] out AvroTypeInfo? info)
    {
        ArgumentNullException.ThrowIfNull(type);
        return s_types.TryGetValue(type, out info);
    }

    private static void Primitive<T>(AvroSchemaType type, AvroWriteAction<T> write, AvroReadFunc<T> read)
    {
        var schema = new PrimitiveSchema(type);
        Register(new AvroTypeInfo<T>(() => schema, write, read, writerSchema => PrimitiveReader(writerSchema, schema, read)));
    }

    // Another primitive writer schema is read with the resolving reader's promotions (int data as a long, and so on).
    private static AvroReadFunc<T> PrimitiveReader<T>(AvroSchema writerSchema, AvroSchema schema, AvroReadFunc<T> read)
    {
        if (writerSchema.HasSameCanonicalForm(schema))
        {
            return read;
        }

        var resolving = Generic.GenericDatumReader.Create(writerSchema, schema);
        return (ref reader) => (T)resolving.Read(ref reader).ToObject()!;
    }

    private static class Cache<T>
    {
        public static AvroTypeInfo<T>? Info;
    }
}
