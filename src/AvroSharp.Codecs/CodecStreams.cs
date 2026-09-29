using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace AvroSharp.Codecs;

/// <summary>
/// Adapters between blocks in memory and the stream APIs of compression libraries. Compiled into each codec package
/// as source, so the packages depend only on AvroSharp's public API.
/// </summary>
internal static class CodecStreams
{
    /// <summary>
    /// Gets whether an exception from a compression library reports corrupt input. Libraries index tables and buffers
    /// with values read from the data, so corrupt blocks also end in <see cref="IndexOutOfRangeException"/> and the
    /// like (#129). The exceptions excluded come from the destination (the reader's size limit is an
    /// <see cref="AvroException"/>) or from no input at all.
    /// </summary>
    public static bool IsCorruptData(Exception ex) =>
        ex is not (InvalidDataException or AvroException or OutOfMemoryException or OperationCanceledException);

    /// <summary>Returns a read-only stream over <paramref name="memory"/>, without copying when it is backed by an array.</summary>
    public static MemoryStream OpenRead(ReadOnlyMemory<byte> memory)
    {
        var segment = MemoryMarshal.TryGetArray(memory, out var array) ? array : new ArraySegment<byte>(memory.ToArray());
        return new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
    }

    /// <summary>Writes all of <paramref name="source"/> to <paramref name="destination"/>, without copying when it is backed by an array.</summary>
    public static void Write(Stream destination, ReadOnlyMemory<byte> source)
    {
        if (MemoryMarshal.TryGetArray(source, out var segment))
        {
            destination.Write(segment.Array!, segment.Offset, segment.Count);
        }
        else
        {
            var copy = source.ToArray();
            destination.Write(copy, 0, copy.Length);
        }
    }

    /// <summary>Reads <paramref name="source"/> to its end into <paramref name="destination"/>.</summary>
    /// <remarks>
    /// Asks the destination for no particular size: a block may end just below the reader's limit, which a large size
    /// hint would exceed.
    /// </remarks>
    public static void CopyTo(Stream source, IBufferWriter<byte> destination)
    {
#if NETSTANDARD2_0
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                destination.Write(chunk.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
#else
        while (true)
        {
            var span = destination.GetSpan();
            var read = source.Read(span);
            if (read == 0)
            {
                return;
            }

            destination.Advance(read);
        }
#endif
    }
}
