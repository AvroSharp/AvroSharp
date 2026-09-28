using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// The generated API beyond Write/Read: schema defaults on construction (#112), writing into caller memory and reusing
/// instances (#115), checks that keep writes valid Avro (#111), and records split into several methods (#113).
/// </summary>
public class GeneratedApiTests
{
    [Test]
    public async Task NewInstances_HaveTheSchemaDefaults()
    {
        var settings = new defaults.Settings();

        await Assert.That(settings.Enabled).IsTrue();
        await Assert.That(settings.Retries).IsEqualTo(3);
        await Assert.That(settings.Timeout).IsEqualTo(-30_000_000_000L);
        await Assert.That(settings.Ratio).IsEqualTo(0.25f);
        await Assert.That(settings.Scale).IsEqualTo(1.5);
        await Assert.That(settings.Label).IsEqualTo("none");
        await Assert.That(settings.Magic).IsEquivalentTo(new byte[] { 0x00, 0xFF });
        await Assert.That(settings.Level).IsEqualTo(defaults.Level.MID);
        await Assert.That(settings.Ports).IsEquivalentTo(new[] { 80, 443 });
        await Assert.That(settings.Limits["b"]).IsEqualTo(2L);
        await Assert.That(settings.Mode).IsEqualTo("auto");
        await Assert.That(settings.Count).IsEqualTo(7);
        await Assert.That(settings.Note).IsNull();
        await Assert.That(settings.Levels).IsEquivalentTo(new[] { defaults.Level.HIGH, defaults.Level.LOW });

        // new T() gives what resolving data with none of the fields gives.
        var empty = AvroSchema.Parse("""{"type":"record","name":"Settings","namespace":"defaults","fields":[]}""");
        var resolved = defaults.Settings.FromAvroBytes([], empty);
        await Assert.That(resolved.ToAvroBytes()).IsEquivalentTo(settings.ToAvroBytes());
    }

    [Test]
    public async Task TryWriteAvroBytes_WritesIntoCallerMemory_OrReportsThatItDoesNotFit()
    {
        var order = TestData.CreateOrder();
        var expected = order.ToAvroBytes();

        var buffer = new byte[expected.Length + 10];
        await Assert.That(order.TryWriteAvroBytes(buffer, out var written)).IsTrue();
        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(buffer.Take(written).ToArray()).IsEquivalentTo(expected);

        // Too small by one byte, and far too small: false, nothing reported written, no exception.
        await Assert.That(order.TryWriteAvroBytes(new byte[expected.Length - 1], out written)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(order.TryWriteAvroBytes(new byte[3], out written)).IsFalse();

        await Assert.That(order.WriteAvroBytes(buffer)).IsEqualTo(expected.Length);
        Assert.Throws<AvroException>(() => order.WriteAvroBytes(new byte[3]));

        var output = new ArrayBufferWriter<byte>();
        order.WriteAvroBytes(output);
        await Assert.That(output.WrittenSpan.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task FromAvroBytes_ReportsBytesConsumed_AndReadsSequences()
    {
        var bytes = TestData.CreateOrder().ToAvroBytes();
        byte[] withTrailer = [.. bytes, 0xAA, 0xBB];

        var order = shop.Order.FromAvroBytes(withTrailer, out var consumed);
        await Assert.That(consumed).IsEqualTo(bytes.Length);
        await Assert.That(order.ToAvroBytes()).IsEquivalentTo(bytes);

        var split = new ReadOnlySequence<byte>(bytes);
        await Assert.That(shop.Order.FromAvroBytes(in split).ToAvroBytes()).IsEquivalentTo(bytes);
    }

    [Test]
    public async Task ReadFrom_FillsAnInstanceAgain_ReusingItsCollections()
    {
        var first = TestData.CreateOrder();
        var second = TestData.CreateOrder();
        second.Counters = [1, 2, 3];
        second.Tags = ["only"];
        second.Attributes = new() { ["z"] = 9 };

        var target = new shop.Order();
        var counters = target.Counters;
        var tags = target.Tags;
        ReadFrom(target, first.ToAvroBytes());
        await Assert.That(target.ToAvroBytes()).IsEquivalentTo(first.ToAvroBytes());
        await Assert.That(ReferenceEquals(target.Counters, counters)).IsTrue();
        await Assert.That(ReferenceEquals(target.Tags, tags)).IsTrue();

        // A second value replaces every field, including shorter collections.
        ReadFrom(target, second.ToAvroBytes());
        await Assert.That(target.ToAvroBytes()).IsEquivalentTo(second.ToAvroBytes());
        await Assert.That(target.Counters).IsEquivalentTo(new long[] { 1, 2, 3 });
        await Assert.That(target.Attributes.Keys).IsEquivalentTo(new[] { "z" });

        // IAvroWritable writes the same bytes as Write.
        await Assert.That(WriteTo(target)).IsEquivalentTo(second.ToAvroBytes());
    }

    [Test]
    public async Task Writes_RejectValuesThatAreNotValidAvro()
    {
        var order = TestData.CreateOrder();
        order.Status = (shop.Status)42;
        var ex = Assert.Throws<AvroException>(() => order.ToAvroBytes());
        await Assert.That(ex.Message).Contains("shop.Order.status");

        // A null-typed field holds only null.
        var nothing = ((RecordSchema)shop.Order.Schema).GetField("nothing").Position;
        var putError = Assert.Throws<AvroException>(() => order.Put(nothing, 1));
        await Assert.That(putError.Message).Contains("shop.Order.nothing");
        order.Put(nothing, null);
    }

    [Test]
    public async Task RecordsWithManyFields_RoundTrip_AndResolve()
    {
        var schema = (RecordSchema)wide.Wide.Schema;
        var generic = new RandomValues(5).TryCreate(schema)!.Value;
        var bytes = GenericDatumWriter.Create(schema).WriteToArray(generic);

        var value = wide.Wide.FromAvroBytes(bytes);
        await Assert.That(value.ToAvroBytes()).IsEquivalentTo(bytes);

        // Another version of the schema with the fields in reverse order goes through the plan's field-by-field reader.
        var fields = string.Join(",", schema.Fields.Reverse().Select(f => $$"""{"name":"{{f.Name}}","type":{{f.Schema.ToJson()}}}"""));
        var writer = (RecordSchema)AvroSchema.Parse($$"""{"type":"record","name":"Wide","namespace":"wide","fields":[{{fields}}]}""");
        var reordered = new GenericRecord(writer);
        foreach (var field in schema.Fields)
        {
            reordered[field.Name] = generic.AsRecord()[field.Name];
        }

        var resolved = wide.Wide.FromAvroBytes(GenericDatumWriter.Create(writer).WriteToArray(reordered), writer);
        await Assert.That(resolved.ToAvroBytes()).IsEquivalentTo(bytes);
    }

#if NET8_0_OR_GREATER
    [Test]
    public async Task GenericApis_UseIAvroSerializable()
    {
        var order = TestData.CreateOrder();
        var bytes = AvroSerializer.Serialize(order);
        await Assert.That(bytes).IsEquivalentTo(order.ToAvroBytes());
        await Assert.That(AvroSerializer.Deserialize<shop.Order>(bytes).ToAvroBytes()).IsEquivalentTo(bytes);
        await Assert.That(AvroSerializer.TrySerialize(order, new byte[bytes.Length], out var written)).IsTrue();
        await Assert.That(written).IsEqualTo(bytes.Length);

        using var file = new System.IO.MemoryStream();
        using (var writer = Containers.AvroFileWriter.Create<shop.Order>(file, new Containers.AvroFileWriterOptions { LeaveOpen = true }))
        {
            writer.Write(order);
            writer.Write(order);
        }

        file.Position = 0;
        using var reader = Containers.AvroFileReader.Open<shop.Order>(file);
        await Assert.That(reader.ReadAll().Count()).IsEqualTo(2);

        using var stream = new System.IO.MemoryStream();
        using (var streamWriter = Streams.AvroStreamWriter.Create<shop.Order>(stream, new Streams.AvroStreamOptions { LeaveOpen = true }))
        {
            streamWriter.Write(order);
        }

        stream.Position = 0;
        using var streamReader = Streams.AvroStreamReader.Open<shop.Order>(stream);
        await Assert.That(streamReader.ReadAll().Single().ToAvroBytes()).IsEquivalentTo(bytes);
    }
#endif

    private static void ReadFrom<T>(T target, byte[] bytes)
        where T : IAvroReadable
    {
        var reader = new AvroReader(bytes);
        target.ReadFrom(ref reader);
    }

    private static byte[] WriteTo<T>(T value)
        where T : IAvroWritable
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        value.WriteTo(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }
}
