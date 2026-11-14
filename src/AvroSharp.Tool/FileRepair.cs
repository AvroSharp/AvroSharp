using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.IO;

namespace AvroSharp.Tool;

/// <summary>
/// <c>avrosharp file repair</c>: copies the blocks of a damaged file that read whole, and skips the rest up to the next
/// sync marker, as avro-tools repair does.
/// </summary>
internal static class FileRepair
{
    // A block decompresses to at most this much, as a reader's default block limit.
    private const int MaxBlockLength = AvroFileReaderOptions.DefaultMaxBlockLength;

    // Objects beyond one per byte that a block may declare: zero-size objects, as a reader allows.
    private const int MaxZeroSizeObjects = 1 << 16;

    /// <summary>Writes the readable blocks of <paramref name="data"/> to <paramref name="output"/>, and describes what was kept and skipped.</summary>
    /// <exception cref="AvroDataException">The header is damaged: without it, nothing can be read.</exception>
    public static string Repair(byte[] data, Stream output, AvroFileReaderOptions options)
    {
        using var file = AvroFileReader.OpenGeneric(new MemoryStream(data), options: options);
        var sync = file.SyncMarker.ToArray();
        var datumReader = GenericDatumReader.Create(file.WriterSchema);
        using var writer = AvroFileWriter.CreateGeneric(output, file.WriterSchema, new AvroFileWriterOptions
        {
            Codec = file.Codec,
            Metadata = FileActions.UserMetadata(file.Metadata),
            LeaveOpen = true,
        });

        long kept = 0;
        long blocks = 0;
        long skipped = 0;
        long regions = 0;
        var position = (int)file.PreviousSync;
        while (position < data.Length)
        {
            if (TryReadBlock(data, position, sync, file.Codec, datumReader, out var values, out var next))
            {
                values.ForEach(value => writer.Write(value));
                kept += values.Count;
                blocks++;
                position = next;
                continue;
            }

            // Damaged: skip to just after the next sync marker, where a block may start.
            var found = data.AsSpan(position + 1).IndexOf(sync);
            var end = found < 0 ? data.Length : position + 1 + found + sync.Length;
            skipped += end - position;
            regions++;
            position = end;
        }

        return regions == 0
            ? $"The file is whole: {kept} objects in {blocks} blocks."
            : $"Kept {kept} objects in {blocks} blocks; skipped {regions} damaged {(regions == 1 ? "region" : "regions")} ({skipped} bytes).";
    }

    // A whole block at position: its count and size, its data, the sync marker after it, and every object decoded.
    private static bool TryReadBlock(byte[] data, int position, byte[] sync, AvroCodec codec, GenericDatumReader datumReader, out List<AvroValue> values, out int next)
    {
        values = [];
        next = position;
        try
        {
            var prefix = new AvroReader(data.AsSpan(position));
            var count = prefix.ReadLong();
            var size = prefix.ReadLong();
            var start = position + (int)prefix.BytesConsumed;
            // A block of no objects is legal when it holds no data.
            if (count < 0 || (count == 0 && size != 0) || size < 0 || size > data.Length - start - sync.Length
                || !data.AsSpan(start + (int)size, sync.Length).SequenceEqual(sync))
            {
                return false;
            }

            var objects = new LimitedOutput();
            codec.Decompress(data.AsMemory(start, (int)size), objects);
            if (count > (long)objects.WrittenCount + MaxZeroSizeObjects)
            {
                return false;
            }

            var reader = new AvroReader(objects.WrittenSpan);
            for (var i = 0L; i < count; i++)
            {
                values.Add(datumReader.Read(ref reader));
            }

            next = start + (int)size + sync.Length;
            return reader.IsAtEnd;
        }
        catch (Exception ex) when (ex is AvroException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A decompression destination that refuses to grow past <see cref="MaxBlockLength"/>.</summary>
    private sealed class LimitedOutput : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _inner = new();

        public int WrittenCount => _inner.WrittenCount;

        public ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;

        // Every write is counted against the limit, whatever size a codec asks for: the memory handed out ends at it.
        public void Advance(int count)
        {
            Check(count);
            _inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Check(Math.Max(sizeHint, 1));
            var memory = _inner.GetMemory(sizeHint);
            return memory[..Math.Min(memory.Length, MaxBlockLength - _inner.WrittenCount)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Check(Math.Max(sizeHint, 1));
            var span = _inner.GetSpan(sizeHint);
            return span[..Math.Min(span.Length, MaxBlockLength - _inner.WrittenCount)];
        }

        private void Check(int count)
        {
            if ((long)_inner.WrittenCount + count > MaxBlockLength)
            {
                throw new InvalidDataException($"A block decompresses to more than {MaxBlockLength} bytes.");
            }
        }
    }
}
