// C# types generated from Schemas/order.avsc: serialize them, write and read a compressed container file, and read
// a file that an older version of the schema wrote.
using System;
using System.IO;
using System.Linq;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using shop;

var orders = Enumerable.Range(1, 1000).Select(i => new Order
{
    Id = Guid.NewGuid(),
    Customer = $"customer-{i % 50}",
    PlacedAt = DateTimeOffset.UnixEpoch.AddDays(20_000 + i),
    Total = 9.99m * i,
    Status = i % 3 == 0 ? Status.SHIPPED : Status.PAID,
    Items = [new Item { Sku = "SKU-" + (i % 7), Quantity = (i % 4) + 1 }],
    Note = i % 10 == 0 ? "gift" : null,
}).ToList();

// One object: no reflection, the generated code calls AvroWriter/AvroReader directly.
byte[] bytes = orders[0].ToAvroBytes();
Order single = Order.FromAvroBytes(bytes);
Console.WriteLine($"One order: {bytes.Length} bytes, total {single.Total}");

// A container file with the zstandard codec from AvroSharp.Codecs.
using var file = new MemoryStream();
using (var writer = AvroFileWriter.Create<Order>(file, new AvroFileWriterOptions { Codec = ZstandardCodec.Default, LeaveOpen = true }))
{
    foreach (var order in orders)
    {
        writer.Write(order);
    }
}

// A reader of files whose codec is not known in advance gets every codec.
file.Position = 0;
var readerOptions = new AvroFileReaderOptions { Codecs = AvroCodecs.All, LeaveOpen = true };
using (var reader = AvroFileReader.Open<Order>(file, readerOptions))
{
    var read = reader.ReadAll().ToList();
    Console.WriteLine($"Container file: {file.Length} bytes ({reader.Codec}), {read.Count} orders");
    Check(read.Count == orders.Count && string.Equals(read[999].Customer, orders[999].Customer, StringComparison.Ordinal) && string.Equals(read[9].Note, "gift", StringComparison.Ordinal));
}

// A file written with version 1 of the schema, before 'status' and 'note' existed: the generated reader resolves it,
// taking the new fields' defaults (Open<T> reads data of another version of T's schema by resolution).
var v1 = AvroSchema.Parse("""
    {"type":"record","name":"Order","namespace":"shop","fields":[
      {"name":"id","type":{"type":"string","logicalType":"uuid"}},
      {"name":"customer","type":"string"},
      {"name":"placed_at","type":{"type":"long","logicalType":"timestamp-millis"}},
      {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
      {"name":"items","type":{"type":"array","items":{"type":"record","name":"Item","fields":[
        {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]}
    """);
var oldRecord = new GenericRecord((RecordSchema)v1)
{
    ["id"] = Guid.Empty.ToString(),
    ["customer"] = "from v1",
    ["placed_at"] = 1_700_000_000_000L,
    ["total"] = new byte[] { 0x01, 0xF4 },  // 500 with scale 2: 5.00
    ["items"] = AvroValue.FromArray([]),
};
using var oldFile = new MemoryStream();
using (var writer = AvroFileWriter.CreateGeneric(oldFile, v1, new AvroFileWriterOptions { LeaveOpen = true }))
{
    writer.Write(oldRecord);
}

oldFile.Position = 0;
using (var reader = AvroFileReader.Open<Order>(oldFile))
{
    var old = reader.ReadAll().Single();
    Console.WriteLine($"Version 1 file read as the current Order: customer '{old.Customer}', status {old.Status}, total {old.Total}");
    Check(old.Status == Status.NEW && old.Note is null && old.Total == 5.00m);
}

Check(single.Id == orders[0].Id && single.Total == orders[0].Total);
Console.WriteLine("OK");
return 0;

static void Check(bool condition)
{
    if (!condition)
    {
        Console.WriteLine("FAILED");
        Environment.Exit(1);
    }
}
