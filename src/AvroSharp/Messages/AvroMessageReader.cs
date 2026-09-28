using System;
using System.Collections.Concurrent;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Messages;

/// <summary>Creates <see cref="AvroMessageReader{T}"/> instances.</summary>
public static class AvroMessageReader
{
    /// <summary>Creates a reader of messages of any schema in <paramref name="store"/>.</summary>
    /// <typeparam name="T">The type read.</typeparam>
    /// <param name="store">Finds each message's writer schema.</param>
    /// <param name="createReader">
    /// Called once per writer schema; returns the function that reads one object of it. For a generated type, return
    /// its <c>Read</c> method when the schema is its own, and a function calling <c>Read(ref reader, writerSchema)</c> otherwise.
    /// </param>
    public static AvroMessageReader<T> Create<T>(IAvroSchemaStore store, Func<AvroSchema, AvroReadFunc<T>> createReader) => new(store, createReader);

    /// <summary>Creates a reader of messages as generic values.</summary>
    /// <param name="store">Finds each message's writer schema.</param>
    /// <param name="readerSchema">The schema to resolve every message to; <see langword="null"/> reads each as written.</param>
    /// <param name="options">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    public static AvroMessageReader<AvroValue> CreateGeneric(IAvroSchemaStore store, AvroSchema? readerSchema = null, GenericDatumReaderOptions? options = null) =>
        new(store, writerSchema =>
        {
            var reader = readerSchema is null
                ? GenericDatumReader.Create(writerSchema, options)
                : GenericDatumReader.Create(writerSchema, readerSchema, options);
            return (ref r) => reader.Read(ref r);
        });

#if NET8_0_OR_GREATER
    /// <summary>Creates a reader of single-object messages of a generated type, resolving other writer schemas.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="store">Finds each message's writer schema.</param>
    public static AvroMessageReader<T> Create<T>(IAvroSchemaStore store)
        where T : IAvroSerializable<T> =>
        new(store, AvroSerializable<T>.For);
#endif
}

/// <summary>
/// Reads single-object encoded messages (see <see cref="AvroMessage"/>): the fingerprint in each header selects the
/// writer schema from an <see cref="IAvroSchemaStore"/>, and the read function for that schema is created once and cached.
/// </summary>
/// <typeparam name="T">The type read.</typeparam>
/// <remarks>Instances are thread-safe when the read functions are.</remarks>
public sealed class AvroMessageReader<T>
{
    private readonly IAvroSchemaStore _store;
    private readonly Func<AvroSchema, AvroReadFunc<T>> _createReader;
    private readonly ConcurrentDictionary<long, CachedReader> _readers = new();

    // Messages usually repeat one schema, so the last entry used is checked before the dictionary. Entries are
    // immutable and the field is read once per message, so concurrent readers always see a matching pair.
    private CachedReader? _last;

    internal AvroMessageReader(IAvroSchemaStore store, Func<AvroSchema, AvroReadFunc<T>> createReader)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(createReader);
        _store = store;
        _createReader = createReader;
    }

    /// <summary>Reads one message.</summary>
    /// <param name="message">The whole message: the header and the object's encoding, with nothing after it.</param>
    /// <exception cref="AvroDataException">
    /// The message has no single-object header, its schema is not in the store, or its data is malformed or has bytes
    /// left over.
    /// </exception>
    public T Read(ReadOnlySpan<byte> message)
    {
        if (!AvroMessage.TryReadHeader(message, out var fingerprint))
        {
            throw new AvroDataException("The data is not a single-object encoded Avro message: it does not start with C3 01 and an 8-byte fingerprint.");
        }

        var cached = _last;
        if (cached is null || cached.Fingerprint != fingerprint)
        {
            cached = _readers.TryGetValue(fingerprint, out var found) ? found : CreateReader(fingerprint);
            _last = cached;
        }

        var reader = new AvroReader(message[AvroMessage.HeaderLength..]);
        var value = cached.Read(ref reader);
        if (!reader.IsAtEnd)
        {
            throw new AvroDataException($"The message has {reader.BytesRemaining} bytes left after its object.");
        }

        return value;
    }

    private CachedReader CreateReader(long fingerprint)
    {
        var schema = _store.GetSchema(fingerprint)
            ?? throw new AvroDataException($"The message was written with a schema whose fingerprint (0x{fingerprint:X16}) is not in the schema store.");
        if (schema.Fingerprint64 != fingerprint)
        {
            throw new AvroException($"The schema store returned a schema whose fingerprint is 0x{schema.Fingerprint64:X16}, not 0x{fingerprint:X16}.");
        }

        var read = _createReader(schema) ?? throw new InvalidOperationException("createReader returned null.");
        return _readers.GetOrAdd(fingerprint, new CachedReader(fingerprint, read));
    }

    private sealed class CachedReader(long fingerprint, AvroReadFunc<T> read)
    {
        public long Fingerprint { get; } = fingerprint;

        public AvroReadFunc<T> Read { get; } = read;
    }
}
