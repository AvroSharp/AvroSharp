using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Buffers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Messages;

/// <summary>Creates <see cref="AvroRegistryMessageReader{T}"/> instances.</summary>
/// <seealso cref="AvroRegistryMessage"/>
/// <seealso cref="AvroRegistryFraming"/>
/// <seealso cref="IAvroSchemaIdResolver"/>
/// <seealso cref="AvroRegistryReaderOptions"/>
public static class AvroRegistryMessageReader
{
    /// <summary>Creates a reader of messages in <paramref name="framing"/>.</summary>
    /// <typeparam name="T">The type read.</typeparam>
    /// <param name="framing">The registry's framing.</param>
    /// <param name="resolver">Finds each message's writer schema by its ID.</param>
    /// <param name="createReader">
    /// Called once per writer schema; returns the function that reads one object of it. For a generated type, return
    /// its <c>Read</c> method when the schema is its own, and a function calling <c>Read(ref reader, writerSchema)</c> otherwise.
    /// </param>
    /// <param name="options">Limits, or <see langword="null"/> for the defaults.</param>
    public static AvroRegistryMessageReader<T> Create<T>(AvroRegistryFraming framing, IAvroSchemaIdResolver resolver, Func<AvroSchema, AvroReadFunc<T>> createReader, AvroRegistryReaderOptions? options = null) =>
        new(framing, resolver, createReader, options ?? AvroRegistryReaderOptions.Default);

    /// <summary>Creates a reader of messages in <paramref name="framing"/> as generic values.</summary>
    /// <param name="framing">The registry's framing.</param>
    /// <param name="resolver">Finds each message's writer schema by its ID.</param>
    /// <param name="readerSchema">The schema to resolve every message to; <see langword="null"/> reads each as written.</param>
    /// <param name="options">Limits, or <see langword="null"/> for the defaults.</param>
    /// <param name="readerOptions">The generic reader's options, or <see langword="null"/> for the defaults.</param>
    public static AvroRegistryMessageReader<AvroValue> CreateGeneric(AvroRegistryFraming framing, IAvroSchemaIdResolver resolver, AvroSchema? readerSchema = null, AvroRegistryReaderOptions? options = null, GenericDatumReaderOptions? readerOptions = null) =>
        Create<AvroValue>(framing, resolver, writerSchema =>
        {
            var reader = readerSchema is null
                ? GenericDatumReader.Create(writerSchema, readerOptions)
                : GenericDatumReader.Create(writerSchema, readerSchema, readerOptions);
            return (ref r) => reader.Read(ref r);
        }, options);

#if NET8_0_OR_GREATER
    /// <summary>Creates a reader of schema-registry messages of a generated type, resolving other writer schemas.</summary>
    /// <typeparam name="T">A generated type.</typeparam>
    /// <param name="framing">The message framing.</param>
    /// <param name="resolver">Finds each message's writer schema by its ID.</param>
    /// <param name="options">Limits, or <see langword="null"/> for the defaults.</param>
    public static AvroRegistryMessageReader<T> Create<T>(AvroRegistryFraming framing, IAvroSchemaIdResolver resolver, AvroRegistryReaderOptions? options = null)
        where T : IAvroSerializable<T> =>
        Create(framing, resolver, AvroSerializable<T>.For, options);
#endif
}

/// <summary>
/// Reads messages in a schema registry's framing (<see cref="AvroRegistryFraming"/>). The ID in each header selects
/// the writer schema through an <see cref="IAvroSchemaIdResolver"/>; the read function for each ID is created once and
/// cached, and the ID seen last is checked before the cache, since streams of messages mostly repeat one schema.
/// </summary>
/// <typeparam name="T">The type read.</typeparam>
/// <remarks>Instances are thread-safe when the read functions and the resolver are.</remarks>
/// <seealso cref="AvroRegistryMessageReader"/>
/// <seealso cref="AvroRegistryMessage"/>
/// <seealso cref="AvroSchemaIdStore"/>
public sealed class AvroRegistryMessageReader<T>
{
    private readonly IAvroSchemaIdResolver _resolver;
    private readonly Func<AvroSchema, AvroReadFunc<T>> _createReader;
    private readonly int _maxPayloadLength;
    private readonly ConcurrentDictionary<AvroSchemaId, Entry> _entries = new();
    private readonly System.Threading.Lock _createLock = new();
    private readonly ConcurrentDictionary<AvroSchemaId, SemaphoreSlim> _fetchGates = new();
    private Entry? _last;

    internal AvroRegistryMessageReader(AvroRegistryFraming framing, IAvroSchemaIdResolver resolver, Func<AvroSchema, AvroReadFunc<T>> createReader, AvroRegistryReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(framing);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(createReader);
        Framing = framing;
        _resolver = resolver;
        _createReader = createReader;
        _maxPayloadLength = options.MaxPayloadLength;
    }

    /// <summary>Gets the framing this reader expects.</summary>
    public AvroRegistryFraming Framing { get; }

    /// <summary>Reads one framed message whose schema the resolver already knows.</summary>
    /// <param name="message">The whole message: the header and the Avro data, with nothing after it.</param>
    /// <exception cref="AvroDataException">
    /// The header is missing or malformed, the resolver does not know the ID (<see cref="ReadAsync"/> can fetch it),
    /// or the data is malformed or has bytes left over.
    /// </exception>
    public T Read(ReadOnlySpan<byte> message)
    {
        var (id, compressed) = ReadHeader(message);
        return Decode(Lookup(id) ?? Create(id, _resolver.GetSchema(id)), message[Framing.HeaderLength..], compressed);
    }

    /// <summary>Reads one framed message, fetching its schema through the resolver when its ID is new.</summary>
    /// <param name="message">The whole message.</param>
    /// <param name="cancellationToken">Cancels a fetch. Concurrent reads of the same new ID fetch it once: the others wait for it.</param>
    /// <exception cref="AvroDataException">
    /// The header is malformed, no schema has the ID, or the data is malformed. Reported through the returned task, as
    /// every other failure is.
    /// </exception>
    public ValueTask<T> ReadAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // Bad data fails the task rather than the call (#160), so reads started together, then awaited, all complete.
        try
        {
            var (id, compressed) = ReadHeader(message.Span);
            return Lookup(id) is { } entry
                ? new(Decode(entry, message.Span[Framing.HeaderLength..], compressed))
                : FetchAndDecodeAsync(id, message[Framing.HeaderLength..], compressed, cancellationToken);
        }
        catch (AvroException ex)
        {
            return new(Task.FromException<T>(ex));
        }
    }

    /// <summary>
    /// Reads the Avro data of a message whose schema ID travels outside it, for example in Confluent's
    /// <c>__value_schema_id</c> header (<see cref="ConfluentSchemaIdHeader"/>).
    /// </summary>
    /// <param name="id">The schema ID.</param>
    /// <param name="payload">The Avro data alone, uncompressed.</param>
    /// <exception cref="AvroDataException">The resolver does not know the ID, or the data is malformed.</exception>
    public T ReadPayload(AvroSchemaId id, ReadOnlySpan<byte> payload) =>
        Decode(Lookup(id) ?? Create(id, _resolver.GetSchema(id)), payload, compressed: false);

    /// <summary>Reads the Avro data of a message whose schema ID travels outside it, fetching the schema when the ID is new.</summary>
    /// <param name="id">The schema ID.</param>
    /// <param name="payload">The Avro data alone, uncompressed.</param>
    /// <param name="cancellationToken">Cancels a fetch. Concurrent reads of the same new ID fetch it once: the others wait for it.</param>
    public ValueTask<T> ReadPayloadAsync(AvroSchemaId id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        try
        {
            return Lookup(id) is { } entry
                ? new(Decode(entry, payload.Span, compressed: false))
                : FetchAndDecodeAsync(id, payload, compressed: false, cancellationToken);
        }
        catch (AvroException ex)
        {
            return new(Task.FromException<T>(ex));
        }
    }

    private (AvroSchemaId Id, bool Compressed) ReadHeader(ReadOnlySpan<byte> message) =>
        Framing.TryReadHeader(message, out var id, out var compressed)
            ? (id, compressed)
            : throw new AvroDataException($"The data is not a {Framing.Name} message: it is shorter than {Framing.HeaderLength} bytes or has another header.");

    private Entry? Lookup(AvroSchemaId id)
    {
        var last = Volatile.Read(ref _last);
        if (last is not null && last.Id == id)
        {
            return last;
        }

        if (_entries.TryGetValue(id, out var entry))
        {
            Volatile.Write(ref _last, entry);
            return entry;
        }

        return null;
    }

    private Entry Create(AvroSchemaId id, AvroSchema? schema)
    {
        if (schema is null)
        {
            throw new AvroDataException($"No schema with ID {id} is known to the resolver.");
        }

        // Under a lock, so createReader runs once per schema, as documented, when several threads meet it at once.
        Entry entry;
        lock (_createLock)
        {
            if (!_entries.TryGetValue(id, out entry!))
            {
                entry = _entries.GetOrAdd(id, new Entry(id, _createReader(schema) ?? throw new InvalidOperationException("createReader returned null.")));
            }
        }

        Volatile.Write(ref _last, entry);
        return entry;
    }

    // One fetch per new ID (#159): concurrent reads of it wait here, then find the entry the first one cached. A fetch
    // that fails or is cancelled caches nothing, so the next waiter fetches with its own token.
    private async ValueTask<T> FetchAndDecodeAsync(AvroSchemaId id, ReadOnlyMemory<byte> payload, bool compressed, CancellationToken cancellationToken)
    {
        var gate = _fetchGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Entry entry;
        try
        {
            entry = Lookup(id) ?? Create(id, await _resolver.GetSchemaAsync(id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }

        return Decode(entry, payload.Span, compressed);
    }

    private T Decode(Entry entry, ReadOnlySpan<byte> payload, bool compressed)
    {
        if (!compressed)
        {
            return DecodePlain(entry, payload);
        }

        using var decompressed = new PooledBufferWriter(Math.Min(Math.Max(payload.Length * 4, 256), _maxPayloadLength));
        Zlib.Decompress(payload.ToArray(), decompressed, _maxPayloadLength);
        return DecodePlain(entry, decompressed.WrittenSpan);
    }

    private static T DecodePlain(Entry entry, ReadOnlySpan<byte> payload)
    {
        var reader = new AvroReader(payload);
        var value = entry.Read(ref reader);
        return reader.IsAtEnd ? value : throw new AvroDataException($"The message has {reader.BytesRemaining} bytes left after its object.");
    }

    private sealed class Entry(AvroSchemaId id, AvroReadFunc<T> read)
    {
        public AvroSchemaId Id { get; } = id;

        public AvroReadFunc<T> Read { get; } = read;
    }
}
