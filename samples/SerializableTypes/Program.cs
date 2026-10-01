// C# types marked [AvroSerializable]: the generator writes their schemas and serializers from their members. Serialize
// them, write and read a container file, read data an older version wrote, and find a type's serializers by Type.
using System;
using System.IO;
using System.Linq;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Shop;

var orders = Enumerable.Range(1, 100).Select(i => new Order
{
    Id = i,
    Customer = $"customer-{i % 10}",
    PlacedAt = DateTimeOffset.UnixEpoch.AddDays(20_000 + i),
    Total = 9.99m * i,
    Status = i % 3 == 0 ? Status.Shipped : Status.Paid,
    Lines = [new OrderLine { Sku = "SKU-" + (i % 7), Quantity = (i % 4) + 1 }],
    Payment = i % 2 == 0 ? new Card { Last4 = "4242" } : new Voucher { Code = "WELCOME" },
    Note = i % 10 == 0 ? "gift" : null,
}).ToList();

// The schema, built from the members: field names in camelCase (the assembly's naming policy), nullable members as
// unions with null, the enum, the nested record and the union of payment records.
Console.WriteLine($"Schema: {Order.SchemaJson[..120]}...");
var schema = (RecordSchema)Order.Schema;
Check(string.Equals(string.Join(",", schema.Fields.Select(f => f.Name)), "id,customer,placedAt,total,status,lines,payment,note", StringComparison.Ordinal));

// One object.
byte[] bytes = orders[0].ToAvroBytes();
Order single = Order.FromAvroBytes(bytes);
Console.WriteLine($"One order: {bytes.Length} bytes, total {single.Total}, paid by {single.Payment?.GetType().Name}");
Check(single.Total == orders[0].Total && single.Payment is Voucher { Code: "WELCOME" });

// A container file, as with types generated from .avsc files.
using var file = new MemoryStream();
using (var writer = AvroFileWriter.Create<Order>(file, new AvroFileWriterOptions { LeaveOpen = true }))
{
    foreach (var order in orders)
    {
        writer.Write(order);
    }
}

file.Position = 0;
using (var reader = AvroFileReader.Open<Order>(file, new AvroFileReaderOptions { LeaveOpen = true }))
{
    var read = reader.ReadAll().ToList();
    Console.WriteLine($"Container file: {file.Length} bytes, {read.Count} orders");
    Check(read.Count == orders.Count && string.Equals(read[9].Note, "gift", StringComparison.Ordinal) && read[1].Payment is Card { Last4: "4242" });
}

// Data written before 'note' and 'payment' existed: the reader takes their defaults (null).
var v1 = AvroSchema.Parse("""
    {"type":"record","name":"Order","namespace":"Shop","fields":[
      {"name":"id","type":"long"},
      {"name":"customer","type":"string"},
      {"name":"placedAt","type":{"type":"long","logicalType":"timestamp-micros"}},
      {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2}},
      {"name":"status","type":{"type":"enum","name":"Status","symbols":["New","Paid","Shipped"]}},
      {"name":"lines","type":{"type":"array","items":{"type":"record","name":"OrderLine","fields":[
        {"name":"sku","type":"string"},{"name":"quantity","type":"int"}]}}}]}
    """);
var oldOrder = new GenericRecord((RecordSchema)v1)
{
    ["id"] = 7L,
    ["customer"] = "grace",
    ["placedAt"] = 0L,
    ["total"] = new byte[] { 0x01, 0xF4 },  // 500 with scale 2: 5.00
    ["status"] = AvroValue.FromEnum((EnumSchema)((RecordSchema)v1).GetField("status").Schema, "New"),
    ["lines"] = AvroValue.FromArray([]),
};
Order old = Order.FromAvroBytes(GenericDatumWriter.Create(v1).WriteToArray(oldOrder), v1);
Console.WriteLine($"Version 1 data: {old.Customer}, total {old.Total}, note is null: {old.Note is null}");
Check(string.Equals(old.Customer, "grace", StringComparison.Ordinal) && old.Total == 5.00m && old.Note is null && old.Payment is null);

// By Type, without reflection: what integrations that get a Type use. Generated types register themselves.
var found = AvroTypes.TryGet(typeof(Order), out var info);
Console.WriteLine($"AvroTypes: {found}, {(info?.Schema as RecordSchema)?.FullName}");
Check(found && info!.Schema is RecordSchema { FullName: "Shop.Order" });

Console.WriteLine("OK");

static void Check(bool condition)
{
    if (!condition)
    {
        Console.WriteLine("FAILED");
        Environment.Exit(1);
    }
}
