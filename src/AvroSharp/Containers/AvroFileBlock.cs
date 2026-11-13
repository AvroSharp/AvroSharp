using System;
using AvroSharp.Schemas;

namespace AvroSharp.Containers;

/// <summary>
/// One block of an object container file as stored: its objects encoded with <see cref="Schema"/> and compressed with
/// <see cref="Codec"/>. <see cref="AvroFileReader{T}.TryReadBlock"/> reads blocks without decoding them, and
/// <see cref="AvroFileWriter{T}.WriteBlock"/> writes them, so files are concatenated or recompressed without decoding
/// their objects, as Apache Avro Java's <c>DataFileStream.nextBlock</c> and <c>DataFileWriter.appendEncoded</c> do.
/// </summary>
/// <seealso cref="AvroFile.Concat"/>
public sealed class AvroFileBlock
{
    /// <summary>Initializes a block. The data is not copied or checked.</summary>
    /// <param name="schema">The schema the objects are encoded with.</param>
    /// <param name="codec">The codec the data is compressed with.</param>
    /// <param name="count">The number of objects, at least 1.</param>
    /// <param name="data">The compressed objects, as stored in a file.</param>
    public AvroFileBlock(AvroSchema schema, AvroCodec codec, long count, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Schema = schema;
        Codec = codec;
        Count = count;
        Data = data;
    }

    /// <summary>Gets the schema the objects are encoded with: the writer schema of the file the block is from.</summary>
    public AvroSchema Schema { get; }

    /// <summary>Gets the codec <see cref="Data"/> is compressed with.</summary>
    public AvroCodec Codec { get; }

    /// <summary>Gets the number of objects in the block.</summary>
    public long Count { get; }

    /// <summary>Gets the objects, compressed with <see cref="Codec"/>, as stored in the file.</summary>
    public ReadOnlyMemory<byte> Data { get; }
}
