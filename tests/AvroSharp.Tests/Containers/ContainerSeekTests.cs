using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;

namespace AvroSharp.Tests.Containers;

/// <summary>Seeking and splitting container files (#32): PreviousSync, Seek, Sync and PastSync, as in the Java implementation.</summary>
public class ContainerSeekTests
{
    [Test]
    public async Task EverySplitPoint_PartitionsTheRecords()
    {
        var rows = ContainerFileTests.Rows(60);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 100 });

        for (var point = 0; point <= file.Length; point++)
        {
            var first = ReadSplit(file, 0, point);
            var second = ReadSplit(file, point, file.Length);
            if (!first.Concat(second).SequenceEqual(rows))
            {
                throw new InvalidOperationException($"Splitting at {point} gave {first.Count} + {second.Count} records, not {rows.Count} in order.");
            }
        }

        await Assert.That(file.Length).IsGreaterThan(1000);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(7)]
    public async Task EvenSplits_PartitionTheRecords(int splits)
    {
        var rows = ContainerFileTests.Rows(500);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { Codec = AvroCodec.Deflate, SyncInterval = 200 });

        var read = new List<AvroValue>();
        for (var i = 0; i < splits; i++)
        {
            read.AddRange(ReadSplit(file, file.Length * i / splits, file.Length * (i + 1) / splits));
        }

        await Assert.That(read.SequenceEqual(rows)).IsTrue();
    }

    [Test]
    public async Task SyncInMeta_SplitsWithoutMistakingTheMarkerInItsMetadata()
    {
        var file = File.ReadAllBytes(TestData.PathOf("apache-avro/syncInMeta.avro"));
        using var whole = AvroFileReader.OpenGeneric(new MemoryStream(file));
        var all = whole.ReadAll().ToList();

        for (var point = 0; point <= file.Length; point += 97)
        {
            var parts = ReadSplit(file, 0, point).Concat(ReadSplit(file, point, file.Length)).ToList();
            if (!parts.SequenceEqual(all))
            {
                throw new InvalidOperationException($"Splitting at {point} gave {parts.Count} records, not {all.Count}.");
            }
        }

        await Assert.That(all.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Seek_ReturnsToABlockThatPreviousSyncReported()
    {
        var rows = ContainerFileTests.Rows(200);
        var file = ContainerFileTests.WriteFile(rows, new AvroFileWriterOptions { SyncInterval = 150 });
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));

        // The first object of each block, by the block's start.
        var blocks = new List<(long Start, long Id)>();
        while (reader.TryRead(out var value))
        {
            if (blocks.Count == 0 || blocks[^1].Start != reader.PreviousSync)
            {
                blocks.Add((reader.PreviousSync, value.AsRecord()["id"].AsInt64()));
            }
        }

        var (start, id) = blocks[blocks.Count / 2];
        reader.Seek(start);
        reader.TryRead(out var again);

        await Assert.That(blocks.Count).IsGreaterThan(5);
        await Assert.That(again.AsRecord()["id"].AsInt64()).IsEqualTo(id);
        await Assert.That(reader.PreviousSync).IsEqualTo(start);
    }

    [Test]
    public async Task SyncPastTheLastMarker_IsTheEnd()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(20), AvroFileWriterOptions.Default);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));

        reader.Sync(file.Length - 10);

        await Assert.That(reader.TryRead(out _)).IsFalse();
        await Assert.That(reader.PastSync(file.Length - 10)).IsTrue();
    }

    [Test]
    public async Task Seeking_NeedsASeekableStream()
    {
        var file = ContainerFileTests.WriteFile(ContainerFileTests.Rows(2), AvroFileWriterOptions.Default);
        using var reader = AvroFileReader.OpenGeneric(new ForwardOnlyStream(file));

        Assert.Throws<NotSupportedException>(() => reader.Sync(0));
        Assert.Throws<NotSupportedException>(() => _ = reader.PreviousSync);

        await Assert.That(reader.ReadAll().Count()).IsEqualTo(2);
    }

    /// <summary>Reads the split [start, end) the way Hadoop-style readers do.</summary>
    private static List<AvroValue> ReadSplit(byte[] file, long start, long end)
    {
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        reader.Sync(start);
        var values = new List<AvroValue>();
        while (reader.TryRead(out var value) && !reader.PastSync(end))
        {
            values.Add(value);
        }

        return values;
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
    }
}
