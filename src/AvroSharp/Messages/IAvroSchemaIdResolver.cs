using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Messages;

/// <summary>
/// Finds writer schemas by registry schema ID: a synchronous lookup of what is already known, and an asynchronous
/// fill that may fetch from a registry. <see cref="AvroRegistryMessageReader{T}"/> uses the lookup on its synchronous
/// path and the fill when <see cref="AvroRegistryMessageReader{T}.ReadAsync"/> meets an ID for the first time.
/// </summary>
/// <seealso cref="AvroSchemaIdStore"/>
/// <seealso cref="AvroRegistryMessageReader"/>
/// <seealso cref="AvroSchemaId"/>
public interface IAvroSchemaIdResolver
{
    /// <summary>Gets the schema with the given ID if it is already known, or <see langword="null"/>.</summary>
    /// <param name="id">The schema ID.</param>
    AvroSchema? GetSchema(AvroSchemaId id);

    /// <summary>Gets the schema with the given ID, fetching it if needed; <see langword="null"/> when it does not exist.</summary>
    /// <param name="id">The schema ID.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default);
}
