using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Glue;
using AvroSharp.Generic;
using AvroSharp.Messages;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Aws.Glue;

/// <summary>
/// Writes and reads Avro messages in AWS Glue Schema Registry's wire format, as AWS's serializer does: the header
/// byte <c>0x03</c>, the compression byte (<c>0x00</c>, or <c>0x05</c> for zlib), the schema version's 16-byte UUID,
/// then the Avro data. It calls Glue through the AWS SDK for .NET's <see cref="IAmazonGlue"/>, and runs on every
/// platform, with no native library.
/// </summary>
/// <remarks>
/// <para>
/// The values are types <see cref="AvroTypes"/> knows (generated from <c>.avsc</c> files, or marked
/// <c>[AvroSerializable]</c>), and generic records (<see cref="GenericRecord"/>, or an <see cref="AvroValue"/>
/// holding one).
/// </para>
/// <para>
/// A schema version is looked up (or registered, with <see cref="AvroSharpGlueOptions.AutoRegisterSchemas"/>) once
/// per schema name and definition, and a version's schema is fetched once per ID. A message is read in its writer's
/// schema and resolved to the type's schema, so data of older and newer versions reads; a generic record is read in
/// the writer's schema. Compressed and uncompressed messages both read.
/// </para>
/// <para>For Confluent.Kafka, <c>AvroSharpGlueKafkaSerializer&lt;T&gt;</c> and <c>AvroSharpGlueKafkaDeserializer&lt;T&gt;</c> of the AvroSharp.Aws.Glue.Kafka package wrap it.</para>
/// </remarks>
public sealed class AvroSharpGlueSerializer
{
    private readonly AvroSharpGlueOptions _options;
    private readonly GlueRegistry _registry;
    private readonly AvroRegistryFraming _framing;
    private readonly ConcurrentDictionary<Type, object> _readers = new();
    private readonly ConditionalWeakTable<AvroSchema, GenericDatumWriter> _genericWriters = new();

    /// <summary>Creates a serializer.</summary>
    /// <param name="glue">The Glue client, such as <c>AmazonGlueClient</c>.</param>
    /// <param name="options">The settings, or <see langword="null"/> for AWS's defaults.</param>
    public AvroSharpGlueSerializer(IAmazonGlue glue, AvroSharpGlueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(glue);
        _options = options?.Copy() ?? new AvroSharpGlueOptions();
        _registry = new GlueRegistry(glue, _options);
        _framing = _options.Compression == AvroSharpGlueCompression.Zlib ? AvroRegistryFraming.AwsGlueCompressed : AvroRegistryFraming.AwsGlue;
    }

    /// <summary>Gets whether <paramref name="message"/> starts with Glue Schema Registry's header, as AWS's <c>CanDecode</c>.</summary>
    /// <param name="message">The message.</param>
    public static bool CanDecode(ReadOnlySpan<byte> message) => AvroRegistryFraming.AwsGlue.TryReadHeader(message, out _);

    /// <summary>Writes a value.</summary>
    /// <typeparam name="T">The value's type: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="transportName">The Kafka topic or Kinesis stream, which names the schema unless <see cref="AvroSharpGlueOptions.SchemaName"/> does.</param>
    /// <param name="cancellationToken">Cancels the registry calls, if any are needed.</param>
    /// <exception cref="InvalidOperationException">
    /// The value isn't of a type the serializer can write, or the registry has no version of its schema and
    /// auto-registration is off, or a new version didn't become available.
    /// </exception>
    public ValueTask<byte[]> SerializeAsync<T>(T value, string transportName, CancellationToken cancellationToken = default) =>
        SerializeAsync(value, transportName, isKey: false, cancellationToken);

    /// <summary>Writes a key or a value.</summary>
    /// <typeparam name="T">The value's type: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
    /// <param name="value">The key or the value.</param>
    /// <param name="transportName">The Kafka topic or Kinesis stream, which names the schema unless <see cref="AvroSharpGlueOptions.SchemaName"/> does.</param>
    /// <param name="isKey">Whether it's a message key, which <see cref="AvroSharpGlueOptions.SchemaNameStrategy"/> can name apart from values.</param>
    /// <param name="cancellationToken">Cancels the registry calls, if any are needed.</param>
    /// <exception cref="InvalidOperationException">
    /// The value isn't of a type the serializer can write, or the registry has no version of its schema and
    /// auto-registration is off, or a new version didn't become available.
    /// </exception>
    public async ValueTask<byte[]> SerializeAsync<T>(T value, string transportName, bool isKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(transportName);
        if (value is GenericRecord or AvroValue)
        {
            AvroValue generic = value is GenericRecord record ? record : (AvroValue)(object)value;
            var recordSchema = generic.Kind == AvroValueKind.Record
                ? generic.AsRecord().Schema
                : throw new ArgumentException("A generic value must be a record, which has its schema.", nameof(value));
            var genericId = await VersionIdAsync(recordSchema, transportName, isKey, cancellationToken).ConfigureAwait(false);
            return AvroRegistryMessage.ToArray(_framing, genericId, generic, _genericWriters.GetValue(recordSchema, s => GenericDatumWriter.Create(s)));
        }

        if (AvroTypes.TryGet<T>(out var typed))
        {
            var typedId = await VersionIdAsync(typed!.Schema, transportName, isKey, cancellationToken).ConfigureAwait(false);
            return AvroRegistryMessage.ToArray(_framing, typedId, value, typed.Write);
        }

        // Typed as a base type or an interface: the value's own type.
        var info = TypeInfo(value.GetType());
        var id = await VersionIdAsync(info.Schema, transportName, isKey, cancellationToken).ConfigureAwait(false);
        return AvroRegistryMessage.ToArray<object>(_framing, id, value, (ref writer, v) => info.WriteObject(ref writer, v));
    }

    /// <summary>Reads a message as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type to read: a type <see cref="AvroTypes"/> knows, <see cref="GenericRecord"/> or <see cref="AvroValue"/>.</typeparam>
    /// <param name="message">The message, compressed or not.</param>
    /// <param name="cancellationToken">Cancels the registry call, if one is needed.</param>
    /// <exception cref="AvroDataException">The message isn't in Glue Schema Registry's wire format, or its data is invalid.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> isn't a type the serializer can read, or the schema version isn't Avro.</exception>
    public async ValueTask<T> DeserializeAsync<T>(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (typeof(T) == typeof(GenericRecord))
        {
            var record = (await GenericReader().ReadAsync(message, cancellationToken).ConfigureAwait(false)).AsRecord();
            return (T)(object)record;
        }

        if (typeof(T) == typeof(AvroValue))
        {
            return (T)(object)await GenericReader().ReadAsync(message, cancellationToken).ConfigureAwait(false);
        }

        var reader = (AvroRegistryMessageReader<T>)_readers.GetOrAdd(typeof(T), _ =>
        {
            var info = AvroTypes.TryGet<T>(out var typed) ? typed! : throw NotKnown(typeof(T));
            return AvroRegistryMessageReader.Create(AvroRegistryFraming.AwsGlue, _registry, info.ReadFor);
        });
        return await reader.ReadAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private AvroRegistryMessageReader<AvroValue> GenericReader() =>
        (AvroRegistryMessageReader<AvroValue>)_readers.GetOrAdd(typeof(AvroValue), _ => AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, _registry));

    private async ValueTask<AvroSchemaId> VersionIdAsync(AvroSchema schema, string transportName, bool isKey, CancellationToken cancellationToken)
    {
        var name = _options.SchemaName ?? _options.SchemaNameStrategy?.Invoke(new AvroSharpGlueSchemaNameContext(transportName, schema, isKey)) ?? transportName;
        return AvroSchemaId.FromGuid(await _registry.VersionIdAsync(name, schema, cancellationToken).ConfigureAwait(false));
    }

    private static AvroTypeInfo TypeInfo(Type type) => AvroTypes.TryGet(type, out var info) ? info! : throw NotKnown(type);

    private static InvalidOperationException NotKnown(Type type) =>
        new($"{type} is not a type AvroSharp knows: generate it from an .avsc file or mark it [AvroSerializable] (with " +
            "AvroSharp.Generators), or use GenericRecord. On .NET Standard, register it first with AvroTypes.Register(" + type.Name + ".AvroTypeInfo).");
}
