using System;
using System.Buffers;
using System.IO.Compression;

namespace AvroSharp.Containers;

/// <summary>
/// A compression codec for the blocks of an object container file. The null and deflate codecs are built in; other
/// codecs (snappy, zstandard, bzip2, xz) come from separate packages, or from any subclass passed to
/// <see cref="AvroFileWriterOptions.Codec"/> and <see cref="AvroFileReaderOptions.Codecs"/>.
/// </summary>
public abstract class AvroCodec
{
    /// <summary>Gets the codec that stores blocks uncompressed.</summary>
    public static AvroCodec Null { get; } = new NullCodec();

    /// <summary>Gets the deflate codec with <see cref="CompressionLevel.Optimal"/> compression.</summary>
    public static AvroCodec Deflate { get; } = new DeflateCodec(CompressionLevel.Optimal);

    /// <summary>Gets the name written to the file's <c>avro.codec</c> metadata entry, for example <c>deflate</c>.</summary>
    public abstract string Name { get; }

    /// <summary>Creates a deflate codec with the given compression level.</summary>
    /// <param name="level">The compression level; it affects writing only.</param>
    public static AvroCodec CreateDeflate(CompressionLevel level) => new DeflateCodec(level);

    /// <summary>Compresses one block.</summary>
    /// <param name="source">The uncompressed block: the encoded objects.</param>
    /// <param name="destination">Receives the compressed block.</param>
    public abstract void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination);

    /// <summary>
    /// Decompresses one block. The destination refuses to grow past the reader's block limit, so an implementation
    /// needs no limit of its own; it should throw <see cref="System.IO.InvalidDataException"/> for corrupt data.
    /// </summary>
    /// <param name="source">The compressed block as stored in the file.</param>
    /// <param name="destination">Receives the encoded objects.</param>
    public abstract void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination);

    /// <inheritdoc/>
    public override string ToString() => Name;

    private sealed class NullCodec : AvroCodec
    {
        public override string Name => AvroCodecNames.Null;

        public override void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination) => destination.Write(source.Span);

        public override void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination) => destination.Write(source.Span);
    }
}
