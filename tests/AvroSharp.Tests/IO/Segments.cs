using System;
using System.Buffers;
using System.Collections.Generic;

namespace AvroSharp.Tests.IO;

/// <summary>Builds multi-segment sequences, including empty segments, to exercise segment-boundary paths.</summary>
internal static class Segments
{
    public static ReadOnlySequence<byte> Split(byte[] data, params int[] cuts)
    {
        var pieces = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        foreach (var cut in cuts)
        {
            pieces.Add(data.AsMemory(start, cut - start));
            start = cut;
        }

        pieces.Add(data.AsMemory(start));
        return Create(pieces);
    }

    /// <summary>One byte per segment, with an empty segment between each: the worst case for the reader.</summary>
    public static ReadOnlySequence<byte> ByteByByte(byte[] data)
    {
        var pieces = new List<ReadOnlyMemory<byte>> { ReadOnlyMemory<byte>.Empty };
        for (var i = 0; i < data.Length; i++)
        {
            pieces.Add(data.AsMemory(i, 1));
            pieces.Add(ReadOnlyMemory<byte>.Empty);
        }

        return Create(pieces);
    }

    private static ReadOnlySequence<byte> Create(List<ReadOnlyMemory<byte>> pieces)
    {
        var first = new Segment(pieces[0], 0);
        var last = first;
        for (var i = 1; i < pieces.Count; i++)
        {
            last = last.Append(pieces[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
