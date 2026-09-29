using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>Malformed or hostile input must fail fast, without large allocations or stack overflows.</summary>
public class HostileInputTests
{
    [Test]
    public async Task ArrayOfRecords_WithAHugeCount_IsRejectedWithoutAllocating()
    {
        // A record with an int field takes at least one byte, so 8 million of them cannot fit in 5 bytes of input.
        var schema = AvroSchema.Parse("""{"type":"array","items":{"type":"record","name":"R","fields":[{"name":"a","type":"int"}]}}""");
        var bytes = Encode(w => w.WriteLong(1L << 23));

#if NET
        var before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(schema).Read(bytes));
        await Assert.That(ex.Message).Contains("larger than the remaining input");
#if NET
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(1024 * 1024);
#endif
    }

    [Test]
    public async Task ZeroSizeItems_AreLimitedPerRead_AcrossBlocks()
    {
        // Empty records take no bytes: three blocks of 30,000 exceed the default budget of 65,536 in total.
        var schema = AvroSchema.Parse("""{"type":"array","items":{"type":"record","name":"Empty","fields":[]}}""");
        var bytes = Encode(w =>
        {
            w.WriteBlockCount(30_000);
            w.WriteBlockCount(30_000);
            w.WriteBlockCount(30_000);
            w.WriteBlockEnd();
        });

        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(schema).Read(bytes));
        await Assert.That(ex.Message).Contains("MaxZeroSizeItems");

        // A higher limit, chosen by the caller, allows it.
        var generous = GenericDatumReader.Create(schema, new GenericDatumReaderOptions { MaxZeroSizeItems = 100_000 });
        await Assert.That(generous.Read(bytes).AsArray().Count).IsEqualTo(90_000);
    }

    /// <summary>
    /// A record of null fields takes no bytes but creates a value per field (#129): each item costs one value plus one
    /// per field against MaxZeroSizeItems, on every read path, so 100 items of 1,000 fields exceed 65,536.
    /// </summary>
    [Test]
    public async Task ZeroSizeRecords_CostTheirFields_OnEveryReadPath()
    {
        var fields = string.Join(",", Enumerable.Range(0, 1_000).Select(i => $$"""{"name":"f{{i}}","type":"null"}"""));
        var wide = $$"""{"type":"record","name":"Wide","fields":[{{fields}}]}""";
        var items = $$"""{"type":"array","items":{{wide}}}""";
        var writer = AvroSchema.Parse($$"""{"type":"record","name":"R","fields":[{"name":"items","type":{{items}}}]}""");
        var reader = AvroSchema.Parse($$"""{"type":"record","name":"R","fields":[{"name":"items","type":{{items}}}]}""");
        var skipOnly = AvroSchema.Parse("""{"type":"record","name":"R","fields":[]}""");
        byte[] Items(long count) => Encode(w =>
        {
            w.WriteBlockCount(count);
            w.WriteBlockEnd();
        });

        var bytes = Items(100);
        var failures = new[]
        {
            Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer).Read(bytes)),
            Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, reader).Read(bytes)),
            Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, skipOnly).Read(bytes)),
            Assert.Throws<AvroDataException>(() =>
            {
                var input = new AvroReader(bytes);
                AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader);
            }),
        };
        foreach (var ex in failures)
        {
            await Assert.That(ex.Message).Contains("MaxZeroSizeItems");
        }

        // 65 items of 1,001 values fit.
        await Assert.That(GenericDatumReader.Create(writer).Read(Items(65)).AsRecord()["items"].AsArray().Count).IsEqualTo(65);
    }

    [Test]
    public async Task DeeplyNestedInput_IsRejectedInsteadOfOverflowingTheStack()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"children","type":{"type":"array","items":"Node"}}]}""");
        var bytes = NestedNodes(depth: 10_000);

        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(schema).Read(bytes));
        await Assert.That(ex.Message).Contains("nested more than 128 levels");
    }

    /// <summary>
    /// A writer schema that nests arrays, maps or unions between recursive records (#129): only records counted
    /// against MaxDepth, so 30 arrays per record level overflowed the stack and crashed the process on every path
    /// that reads by the writer's schema, including skipping and transcoding for generated types.
    /// </summary>
    [Test]
    [Arguments("array")]
    [Arguments("map")]
    [Arguments("union")]
    public async Task NestingThroughCollectionsAndUnions_IsBounded_OnEveryReadPath(string kind)
    {
        const int Levels = 60;
        var type = "\"R\"";
        for (var i = 0; i < Levels; i++)
        {
            type = kind switch
            {
                "array" => $$"""{"type":"array","items":{{type}}}""",
                "map" => $$"""{"type":"map","values":{{type}}}""",
                _ => $$"""["null",{"type":"array","items":{{type}}}]""",
            };
        }

        var writer = AvroSchema.Parse($$"""{"type":"record","name":"R","fields":[{"name":"f","type":{{type}}}]}""");
        var reader = AvroSchema.Parse("""{"type":"record","name":"R","fields":[{"name":"g","type":"int","default":0}]}""");

        // Each level opens one item: a block count of 1 (and an empty map key), or the union's array branch and
        // a block count of 1 (a union cannot hold a union directly).
        var level = kind switch
        {
            "array" => new byte[] { 0x02 },
            "map" => new byte[] { 0x02, 0x00 },
            _ => new byte[] { 0x02, 0x02 },
        };
        var bytes = Enumerable.Repeat(level, 128 * Levels).SelectMany(b => b).ToArray();

        var generic = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer).Read(bytes));
        var resolving = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(writer, reader).Read(bytes));
        var transcoded = Assert.Throws<AvroDataException>(() =>
        {
            var input = new AvroReader(bytes);
            AvroSharp.Serialization.AvroGeneratedCode.ResolveToReaderEncoding(ref input, writer, reader);
        });
        var skipped = Assert.Throws<AvroDataException>(() =>
        {
            var plan = AvroSharp.Serialization.AvroGeneratedCode.GetRecordPlan(writer, reader)!;
            var input = new AvroReader(bytes);
            plan.Skip(0, ref input);
        });

        foreach (var ex in new[] { generic, resolving, transcoded, skipped })
        {
            await Assert.That(ex.Message).Contains("nested more than 1024 levels deep, counting arrays, maps and records");
        }
    }

    /// <summary>
    /// With the depth limits raised past what a thread's stack holds, the stack check reports the input instead of
    /// the process ending in a stack overflow (#129). The check runs every few levels, not per record.
    /// </summary>
    [Test]
    public async Task NestingBeyondTheThreadsStack_IsRejected()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"children","type":{"type":"array","items":"Node"}}]}""");
        var reader = GenericDatumReader.Create(schema, new GenericDatumReaderOptions { MaxDepth = 1_000_000 });
        var bytes = NestedNodes(depth: 50_000);

        Exception? failure = null;
        var thread = new System.Threading.Thread(
            () =>
            {
                try
                {
                    reader.Read(bytes);
                }
#pragma warning disable CA1031 // Handed to the test thread, which asserts on it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failure = ex;
                }
            },
            maxStackSize: 512 * 1024);
        thread.Start();
        thread.Join();

        await Assert.That(failure).IsTypeOf<AvroDataException>();
        await Assert.That(failure!.Message).Contains("nested too deeply for the thread's stack");
    }

    [Test]
    public async Task MaxDepth_AllowsNestingUpToTheLimit()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"children","type":{"type":"array","items":"Node"}}]}""");
        var reader = GenericDatumReader.Create(schema, new GenericDatumReaderOptions { MaxDepth = 200 });

        var value = reader.Read(NestedNodes(depth: 200));
        Assert.Throws<AvroDataException>(() => reader.Read(NestedNodes(depth: 201)));

        var depth = 0;
        for (var node = value; node.AsRecord()["children"].AsArray().Count > 0; node = node.AsRecord()["children"].AsArray()[0])
        {
            depth++;
        }

        await Assert.That(depth + 1).IsEqualTo(200);
    }

    [Test]
    public async Task RecordContainingItself_IsRejectedByTheWriter()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Loop","fields":[{"name":"next","type":["null","Loop"]}]}""");
        var record = new GenericRecord(schema);
        record["next"] = record;

        var ex = Assert.Throws<AvroException>(() => GenericDatumWriter.Create(schema).WriteToArray(record));
        await Assert.That(ex.Message).Contains("nested more than 128 levels");
    }

    [Test]
    public async Task WriterMaxDepth_IsConfigurable()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"List","fields":[{"name":"next","type":["null","List"]}]}""");
        var head = new GenericRecord(schema);
        var current = head;
        for (var i = 1; i < 10; i++)
        {
            var next = new GenericRecord(schema);
            current["next"] = next;
            current = next;
        }

        // A chain of 10 records: 10 levels are allowed with MaxDepth = 10, and rejected with 9.
        Assert.Throws<AvroException>(() => GenericDatumWriter.Create(schema, new GenericDatumWriterOptions { MaxDepth = 9 }).WriteToArray(head));
        var bytes = GenericDatumWriter.Create(schema, new GenericDatumWriterOptions { MaxDepth = 10 }).WriteToArray(head);
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task MapWithAHugeCount_IsRejected()
    {
        var schema = AvroSchema.Parse("""{"type":"map","values":"long"}""");
        var bytes = Encode(w => w.WriteLong(1L << 40));
        var ex = Assert.Throws<AvroDataException>(() => GenericDatumReader.Create(schema).Read(bytes));
        await Assert.That(ex.Message).Contains("larger than the remaining input");
    }

    /// <summary>A chain of <paramref name="depth"/> Node records, each holding a one-item array of the next.</summary>
    private static byte[] NestedNodes(int depth) => Encode(w =>
    {
        for (var i = 0; i < depth - 1; i++)
        {
            w.WriteBlockCount(1);
        }

        foreach (var _ in Enumerable.Range(0, depth))
        {
            w.WriteBlockEnd();
        }
    });

    private static byte[] Encode(Action<Writer> write)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new Writer(output);
        write(writer);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Appends values to a buffer; a class, so the lambdas above need no ref struct.</summary>
    private sealed class Writer(ArrayBufferWriter<byte> output)
    {
        public void WriteLong(long value)
        {
            var writer = new AvroWriter(output);
            writer.WriteLong(value);
            writer.Flush();
        }

        public void WriteBlockCount(long count)
        {
            var writer = new AvroWriter(output);
            writer.WriteBlockCount(count);
            writer.Flush();
        }

        public void WriteBlockEnd()
        {
            var writer = new AvroWriter(output);
            writer.WriteBlockEnd();
            writer.Flush();
        }
    }
}