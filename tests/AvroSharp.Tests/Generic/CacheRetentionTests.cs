using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization.Generated;

namespace AvroSharp.Tests.Generic;

/// <summary>
/// The caches of schema pairs must not keep writer schemas alive for as long as the reader schema lives (#129): each
/// file opened with a reader schema brings a writer schema of its own, parsed from the file's header.
/// </summary>
[NotInParallel]
public class CacheRetentionTests
{
    private const int Writers = 50;

    [Test]
    public async Task WriterSchemas_AreCollected_WhileTheReaderSchemaLives()
    {
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"},{"name":"b","type":"int","default":0}]}""");
        var writers = UseWriters(reader);

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var alive = 0;
        foreach (var writer in writers)
        {
            alive += writer.IsAlive ? 1 : 0;
        }

        await Assert.That(alive).IsEqualTo(0);
        GC.KeepAlive(reader);
    }

    // Fills the resolving reader, plan and transcoder caches with fresh writer schemas; returns weak references to them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] UseWriters(AvroSchema reader)
    {
        var writers = new WeakReference[Writers];
        for (var i = 0; i < Writers; i++)
        {
            var writer = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}""");
            _ = GenericDatumReader.Create(writer, reader).Read(new byte[] { 0x02 });
            _ = GenericDatumReader.GetRecordPlan(writer, reader);
            var input = new AvroReader(new byte[] { 0x02 });
            _ = AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader);
            writers[i] = new WeakReference(writer);
        }

        return writers;
    }
}
