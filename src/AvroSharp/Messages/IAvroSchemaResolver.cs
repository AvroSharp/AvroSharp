using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Messages;

/// <summary>
/// Finds writer schemas by their CRC-64-AVRO fingerprint, for reading single-object encoded messages: a synchronous
/// lookup of what is already known, and an asynchronous one that may fetch. <see cref="AvroMessageReader{T}"/> uses the
/// lookup in <see cref="AvroMessageReader{T}.Read"/> and the asynchronous one when
/// <see cref="AvroMessageReader{T}.ReadAsync"/> meets a fingerprint for the first time.
/// <see cref="AvroSchemaStore"/> is the in-memory implementation.
/// </summary>
/// <seealso cref="AvroSchemaStore"/>
/// <seealso cref="AvroMessageReader"/>
/// <seealso cref="AvroMessage"/>
public interface IAvroSchemaResolver
{
    /// <summary>Gets the schema with the given fingerprint if it is already known, or <see langword="null"/>.</summary>
    /// <param name="fingerprint">The schema's <see cref="AvroSchema.Fingerprint64"/>.</param>
    AvroSchema? GetSchema(long fingerprint);

    /// <summary>Gets the schema with the given fingerprint, fetching it if needed; <see langword="null"/> when it does not exist.</summary>
    /// <param name="fingerprint">The schema's <see cref="AvroSchema.Fingerprint64"/>.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    ValueTask<AvroSchema?> GetSchemaAsync(long fingerprint, CancellationToken cancellationToken = default);
}
