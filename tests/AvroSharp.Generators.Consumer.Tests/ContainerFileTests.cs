using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Generated types written to and read from object container files (#32).</summary>
public class ContainerFileTests
{
    [Test]
    [Arguments("null")]
    [Arguments("deflate")]
    public async Task GeneratedRecords_RoundTripThroughAFile(string codec)
    {
        var orders = Enumerable.Range(0, 200).Select(i =>
        {
            var order = TestData.CreateOrder();
            order.Id = i;
            return order;
        }).ToList();

        using var file = new MemoryStream();
        using (var writer = AvroFileWriter.Create<shop.Order>(file, shop.Order.Schema, shop.Order.Write, new AvroFileWriterOptions
        {
            Codec = string.Equals(codec, AvroCodecNames.Deflate, StringComparison.Ordinal) ? AvroCodec.Deflate : AvroCodec.Null,
            SyncInterval = 1024,
            LeaveOpen = true,
        }))
        {
            foreach (var order in orders)
            {
                writer.Write(order);
            }
        }

        file.Position = 0;
        using var reader = AvroFileReader.Open<shop.Order>(file, _ => shop.Order.Read);
        var read = reader.ReadAll().ToList();

        await Assert.That(read.Select(o => o.Id).SequenceEqual(orders.Select(o => o.Id))).IsTrue();
        await Assert.That(read.Zip(orders, (a, b) => a.ToAvroBytes().AsSpan().SequenceEqual(b.ToAvroBytes())).All(same => same)).IsTrue();
    }

    [Test]
    public async Task AFileOfAnOlderSchema_IsResolvedToTheGeneratedType()
    {
        var writerSchema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Person","namespace":"evo","fields":[
              {"name":"id","type":"int"},
              {"name":"nickname","type":"string"},
              {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["BASIC","GOLD"]}}
            ]}
            """);
        using var file = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(file, writerSchema, new AvroFileWriterOptions { LeaveOpen = true }))
        {
            writer.Write(new GenericRecord(writerSchema)
            {
                ["id"] = 7,
                ["nickname"] = "Ada",
                ["tier"] = AvroValue.FromEnum((EnumSchema)writerSchema.GetField("tier").Schema, "GOLD"),
            });
        }

        file.Position = 0;
        using var reader = AvroFileReader.Open<evo.Person>(file, ws => (ref AvroReader r) => evo.Person.Read(ref r, ws));
        var person = reader.ReadAll().Single();

        await Assert.That(person.Id).IsEqualTo(7L);
        await Assert.That(person.Name).IsEqualTo("Ada");
        await Assert.That(person.Tier).IsEqualTo(evo.Tier.GOLD);
    }
}
