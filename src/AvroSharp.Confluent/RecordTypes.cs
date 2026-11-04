using System;
using System.Collections.Generic;
using System.Linq;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Confluent;

/// <summary>
/// Several record types read and written as one <typeparamref name="T"/>, for a topic that carries them all (with the
/// <c>TopicRecord</c> or <c>Record</c> subject name strategy, or a top-level union of records under one subject): a
/// type whose schema is the union of the records, which reads a message as the type of the writer's record.
/// </summary>
/// <typeparam name="T">A type every record type is: <see cref="object"/>, or their base type or interface.</typeparam>
internal sealed class RecordTypes<T>
{
    private readonly AvroTypeInfo[] _infos;

    // The record types by their records' full names and aliases, and by their .NET types.
    private readonly Dictionary<string, AvroTypeInfo> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, int> _index = [];

    public RecordTypes(IEnumerable<Type> types, string parameter)
    {
        ArgumentNullException.ThrowIfNull(types, parameter);
        var infos = new List<AvroTypeInfo>();
        foreach (var type in types)
        {
            if (type is null)
            {
                throw new ArgumentException("A record type is null.", parameter);
            }

            if (!typeof(T).IsAssignableFrom(type))
            {
                throw new ArgumentException($"{type} is not a {typeof(T)}, so it can't be read or written as one.", parameter);
            }

            var info = Known(type);
            var record = info.Schema as RecordSchema
                ?? throw new ArgumentException($"The schema of {type} is not a record, so a message's schema can't name it.", parameter);
            if (!_index.TryAdd(type, infos.Count))
            {
                throw new ArgumentException($"{type} is listed twice.", parameter);
            }

            foreach (var name in record.Aliases.Select(alias => alias.FullName).Prepend(record.FullName))
            {
                if (_byName.TryGetValue(name, out var other))
                {
                    throw new ArgumentException($"{other.Type} and {type} both have the record name or alias {name}.", parameter);
                }

                _byName.Add(name, info);
            }

            infos.Add(info);
        }

        if (infos.Count == 0)
        {
            throw new ArgumentException("No record types are given: list the types the topic carries.", parameter);
        }

        _infos = [.. infos];
        var union = new UnionSchema(_infos.Select(info => info.Schema));
        Info = new AvroTypeInfo<T>(() => union, Write, Read, ReadFor);
    }

    /// <summary>Gets the type whose schema is the union of the records.</summary>
    public AvroTypeInfo<T> Info { get; }

    /// <summary>Gets the type of a value, as <see cref="object"/>, if it is one of the record types.</summary>
    public AvroTypeInfo<object>? AsObject(Type type) => _index.TryGetValue(type, out var i) ? AsObject(_infos[i]) : null;

    /// <summary>Gets the record types' .NET types, for messages.</summary>
    public string Names => string.Join(", ", _infos.Select(info => info.Type.ToString()));

    // The type AvroTypes knows: a generated or [AvroSerializable] type registers itself.
    private static AvroTypeInfo Known(Type type) =>
        AvroTypes.TryGet(type, out var info)
            ? info
            : throw new InvalidOperationException(
                $"{type} is not a type AvroSharp knows: generate it from an .avsc file or mark it [AvroSerializable] (with " +
                "AvroSharp.Generators). On .NET Standard, register it first with AvroTypes.Register.");

    // The type as AvroTypeInfo<object>: the boxed read and write functions of its non-generic info.
    private static AvroTypeInfo<object> AsObject(AvroTypeInfo info) =>
        new(
            () => info.Schema,
            (ref writer, value) => info.WriteObject(ref writer, value),
            (ref reader) => info.ReadObject(ref reader)!,
            writerSchema =>
            {
                var read = info.ReadObjectFor(writerSchema);
                return (ref reader) => read(ref reader)!;
            });

    // A value of the union: its branch, then the record.
    private void Write(ref AvroWriter writer, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_index.TryGetValue(value.GetType(), out var i))
        {
            throw new InvalidOperationException($"{value.GetType()} is not one of the record types ({Names}).");
        }

        writer.WriteLong(i);
        _infos[i].WriteObject(ref writer, value);
    }

    private T Read(ref AvroReader reader)
    {
        var i = reader.ReadLong();
        return i >= 0 && i < _infos.Length
            ? (T)_infos[i].ReadObject(ref reader)!
            : throw new AvroException($"The union branch {i} is out of range: the union has {_infos.Length}.");
    }

    // A record is read as the type of its name; a union (a topic of several records under one subject) as the type
    // of the branch each message holds.
    private AvroReadFunc<T> ReadFor(AvroSchema writer)
    {
        switch (writer)
        {
            case RecordSchema record:
                var read = ReadRecord(record) ?? throw new AvroSchemaException($"No record type has the record {record.FullName}: the types are {Names}.");
                return (ref reader) => read(ref reader);
            case UnionSchema union:
                var branches = union.Branches.Select(branch => branch is RecordSchema r ? ReadRecord(r) : null).ToArray();
                if (branches.All(branch => branch is null))
                {
                    throw new AvroSchemaException($"No branch of the union {union.CanonicalForm} is a record of one of the types ({Names}).");
                }

                return (ref reader) =>
                {
                    var i = reader.ReadLong();
                    return i >= 0 && i < branches.Length && branches[i] is { } branch
                        ? branch(ref reader)
                        : throw new AvroException($"Branch {i} of the writer's union is not a record of one of the types ({Names}).");
                };
            default:
                throw new AvroSchemaException($"The writer's schema {writer.CanonicalForm} is not a record, or a union of records, so it names none of the types ({Names}).");
        }
    }

    private AvroReadFunc<T>? ReadRecord(RecordSchema record)
    {
        if (!_byName.TryGetValue(record.FullName, out var info))
        {
            return null;
        }

        var read = info.ReadObjectFor(record);
        return (ref reader) => (T)read(ref reader)!;
    }
}
