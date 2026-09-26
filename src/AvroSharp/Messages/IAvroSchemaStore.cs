using AvroSharp.Schemas;

namespace AvroSharp.Messages;

/// <summary>Finds writer schemas by their CRC-64-AVRO fingerprint, for reading single-object encoded messages.</summary>
public interface IAvroSchemaStore
{
    /// <summary>Gets the schema with the given fingerprint, or <see langword="null"/> when it is unknown.</summary>
    /// <param name="fingerprint">The schema's <see cref="AvroSchema.Fingerprint64"/>.</param>
    AvroSchema? GetSchema(long fingerprint);
}
