using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Messages;

/// <summary>Creates <see cref="AvroMessageReader{T}"/> instances.</summary>
/// <seealso cref="AvroMessage"/>
/// <seealso cref="IAvroSchemaResolver"/>
/// <seealso cref="AvroSchemaStore"/>
public static class AvroMessageReader
{
    /// <summary>Creates a reader of messages of any schema <paramref name="resolver"/> finds.</summary>
    /// <typeparam name="T">The type read.</typeparam>
    /// <param name="resolver">Finds each message's writer schema.</param>
    /// <param name="createReader">
    /// Called once per writer schema; returns the function that reads one object of it. For a generated type, return
    /// its <c>Read</c> method when the schema is its own, and a function calling <c>Read(ref reader, writerSchema)</c> otherwise.
    /// </param>
    public static AvroMessageReader<T> Create<T>(IAvroSchemaResolver resolver, Func<AvroSchema, AvroReadFunc<T>> createReader) => new(resolver, createReader);

    /// <summary>Creates a reader of messages as generic values.</summary>
    /// <param name="resolver">Finds each message's writer schema.</param>
    /// <param name="readerSchema">The schema to resolve every message to; <see langword="null"/> reads each as written.</param>
    /// <param name="readerOptions">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    public static AvroMessageReader<AvroValue> CreateGeneric(IAvroSchemaResolver resolver, AvroSchema? readerSchema = null, GenericDatumReaderOptions? readerOptions = null) =>
        new(resolver, writerSchema =>
        {
            var reader = readerSchema is null
                ? GenericDatumReader.Create(writerSchema, readerOptions)
                : GenericDatumReader.Create(writerSchema, readerSchema, readerOptions);
            return (ref r) => reader.Read(ref r);
        });

#if NET8_0_OR_GREATER
    /// <summary>Creates a reader of single-object messages of a generated type, resolving other writer schemas.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="resolver">Finds each message's writer schema.</param>
    public static AvroMessageReader<T> Create<T>(IAvroSchemaResolver resolver)
        where T : IAvroSerializable<T> =>
        new(resolver, AvroSerializable<T>.For);
#endif
}

/// <summary>
/// Reads single-object encoded messages (see <see cref="AvroMessage"/>): the fingerprint in each header selects the
/// writer schema through an <see cref="IAvroSchemaResolver"/>, and the read function for that schema is created once and cached.
/// </summary>
/// <typeparam name="T">The type read.</typeparam>
/// <remarks>Instances are thread-safe when the read functions are.</remarks>
/// <seealso cref="AvroMessageReader"/>
/// <seealso cref="AvroMessage"/>
/// <seealso cref="AvroSchemaStore"/>
public sealed class AvroMessageReader<T>
{
    private readonly IAvroSchemaResolver _resolver;
    private readonly Func<AvroSchema, AvroReadFunc<T>> _createReader;
    private readonly ConcurrentDictionary<long, CachedReader> _readers = new();
    private readonly System.Threading.Lock _createLock = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _fetchGates = new();

    // Messages usually repeat one schema, so the last entry used is checked before the dictionary. Entries are
    // immutable and the field is read once per message, so concurrent readers always see a matching pair.
    private CachedReader? _last;

    internal AvroMessageReader(IAvroSchemaResolver resolver, Func<AvroSchema, AvroReadFunc<T>> createReader)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(createReader);
        _resolver = resolver;
        _createReader = createReader;
    }

    /// <summary>Reads one message.</summary>
    /// <param name="message">The whole message: the header and the object's encoding, with nothing after it.</param>
    /// <exception cref="AvroDataException">
    /// The message has no single-object header, the resolver does not know its schema, or its data is malformed or has
    /// bytes left over.
    /// </exception>
    public T Read(ReadOnlySpan<byte> message)
    {
        // The common case inline: the schema of the last message. A shared Lookup/Decode path cost ~2 ns per
        // message (5%) on small objects; ReadAsync, which awaits anyway, uses the helpers.
        if (!AvroMessage.TryReadHeader(message, out var fingerprint))
        {
            throw NotAMessage();
        }

        var cached = _last;
        if (cached is null || cached.Fingerprint != fingerprint)
        {
            cached = Find(fingerprint) ?? CreateReader(fingerprint, _resolver.GetSchema(fingerprint));
        }

        var reader = new AvroReader(message[AvroMessage.HeaderLength..]);
        var value = cached.Read(ref reader);
        if (!reader.IsAtEnd)
        {
            throw BytesLeft(reader.BytesRemaining);
        }

        return value;
    }

    /// <summary>Reads one message, fetching its schema through the resolver when its fingerprint is new.</summary>
    /// <param name="message">The whole message: the header and the object's encoding, with nothing after it.</param>
    /// <param name="cancellationToken">Cancels a fetch. Concurrent reads of the same new fingerprint fetch it once: the others wait for it.</param>
    /// <exception cref="AvroDataException">
    /// The message has no single-object header, the resolver does not know its schema, or its data is malformed or has
    /// bytes left over. Reported through the returned task, as every other failure is.
    /// </exception>
    public ValueTask<T> ReadAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // Bad data fails the task rather than the call (#160), so reads started together, then awaited, all complete.
        try
        {
            var fingerprint = ReadHeader(message.Span);
            return Lookup(fingerprint) is { } cached
                ? new(Decode(cached, message.Span))
                : FetchAndDecodeAsync(fingerprint, message, cancellationToken);
        }
        catch (AvroException ex)
        {
            return new(Task.FromException<T>(ex));
        }
    }

    private static long ReadHeader(ReadOnlySpan<byte> message) =>
        AvroMessage.TryReadHeader(message, out var fingerprint) ? fingerprint : throw NotAMessage();

    private static AvroDataException NotAMessage() =>
        new("The data is not a single-object encoded Avro message: it does not start with C3 01 and an 8-byte fingerprint.");

    private static AvroDataException BytesLeft(long count) => new($"The message has {count} bytes left after its object.");

    private CachedReader? Lookup(long fingerprint)
    {
        var cached = _last;
        return cached is not null && cached.Fingerprint == fingerprint ? cached : Find(fingerprint);
    }

    // A schema other than the last message's: the cache, which becomes the last one.
    private CachedReader? Find(long fingerprint)
    {
        if (_readers.TryGetValue(fingerprint, out var cached))
        {
            _last = cached;
            return cached;
        }

        return null;
    }

    // One fetch per new fingerprint (#159): concurrent reads of it wait here, then find the reader the first one cached.
    // A fetch that fails or is cancelled caches nothing, so the next waiter fetches with its own token.
    private async ValueTask<T> FetchAndDecodeAsync(long fingerprint, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var gate = _fetchGates.GetOrAdd(fingerprint, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CachedReader cached;
        try
        {
            cached = Find(fingerprint) ?? CreateReader(fingerprint, await _resolver.GetSchemaAsync(fingerprint, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }

        return Decode(cached, message.Span);
    }

    private static T Decode(CachedReader cached, ReadOnlySpan<byte> message)
    {
        var reader = new AvroReader(message[AvroMessage.HeaderLength..]);
        var value = cached.Read(ref reader);
        if (!reader.IsAtEnd)
        {
            throw BytesLeft(reader.BytesRemaining);
        }

        return value;
    }

    private CachedReader CreateReader(long fingerprint, AvroSchema? schema)
    {
        if (schema is null)
        {
            throw new AvroDataException($"The message was written with a schema whose fingerprint (0x{fingerprint:X16}) the schema resolver does not know.");
        }

        if (schema.Fingerprint64 != fingerprint)
        {
            throw new AvroException($"The schema resolver returned a schema whose fingerprint is 0x{schema.Fingerprint64:X16}, not 0x{fingerprint:X16}.");
        }

        // Under a lock, so createReader runs once per schema, as documented, when several threads meet it at once.
        lock (_createLock)
        {
            if (_readers.TryGetValue(fingerprint, out var cached))
            {
                return cached;
            }

            var read = _createReader(schema) ?? throw new InvalidOperationException("createReader returned null.");
            var created = _readers.GetOrAdd(fingerprint, new CachedReader(fingerprint, read));
            _last = created;
            return created;
        }
    }

    private sealed class CachedReader(long fingerprint, AvroReadFunc<T> read)
    {
        public long Fingerprint { get; } = fingerprint;

        public AvroReadFunc<T> Read { get; } = read;
    }
}
