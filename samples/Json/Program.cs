// Avro's JSON encoding with the generic data model: write and read JSON, how it differs from plain JSON, convert
// binary Avro to JSON and back, read JSON written with an older schema version, and handle errors.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AvroSharp;
using AvroSharp.Generic;
using AvroSharp.Schemas;

var orderSchema = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Order","namespace":"shop","fields":[
      {"name":"id","type":"long"},
      {"name":"customer","type":"string"},
      {"name":"status","type":{"type":"enum","name":"OrderStatus","symbols":["PENDING","SHIPPED","DELIVERED"]}},
      {"name":"coupon","type":["null","string"],"default":null},
      {"name":"note","type":["null","string"],"default":null},
      {"name":"items","type":{"type":"map","values":"int"}},
      {"name":"clientIp","type":{"type":"fixed","name":"IPv4","size":4}}]}
    """);

// A record with an enum, two unions (one null), a map and a fixed.
var order = new GenericRecord(orderSchema)
{
    ["id"] = 1042L,
    ["customer"] = "Grace Hopper",
    ["status"] = AvroValue.FromEnum((EnumSchema)orderSchema.GetField("status").Schema, "SHIPPED"),
    ["coupon"] = AvroValue.Null,
    ["note"] = "Leave at the door",
    ["items"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["SKU-1001"] = 2, ["SKU-2040"] = 1 }),
    ["clientIp"] = new GenericFixed((FixedSchema)orderSchema.GetField("clientIp").Schema, [192, 168, 1, 20]),
};

// Write a value as JSON. Writers are cached per schema and are thread-safe.
var orderJson = GenericDatumJsonWriter.Create(orderSchema);
string json = orderJson.WriteToString(order);
Console.WriteLine(json);

// The same, indented, or as UTF-8 bytes.
string indented = orderJson.WriteToString(order, indented: true);
byte[] utf8 = orderJson.WriteToUtf8Bytes(order);

// Read it back. The reader takes the schema the JSON was written with.
AvroValue fromJson = GenericDatumJsonReader.Create(orderSchema).Read(json);
GenericRecord copy = fromJson.AsRecord();
Console.WriteLine($"Read back: {copy["customer"].AsString()}, {copy["status"].AsEnumSymbol()}");

// Bytes, like fixed, are a string whose code points 0-255 are the bytes.
string bytesJson = GenericDatumJsonWriter.Create(AvroSchema.Parse("\"bytes\"")).WriteToString(new byte[] { 0x48, 0x69, 0x00, 0xFF });
Console.WriteLine(bytesJson);

// Binary Avro to JSON, here inside a larger JSON document such as a REST API response.
byte[] binary = GenericDatumWriter.Create(orderSchema).WriteToArray(order);
AvroValue decoded = GenericDatumReader.Create(orderSchema).Read(binary);
var buffer = new ArrayBufferWriter<byte>();
using (var writer = new Utf8JsonWriter(buffer))
{
    writer.WriteStartObject();
    writer.WriteString("schema", orderSchema.FullName);
    writer.WritePropertyName("order");
    orderJson.Write(writer, decoded);
    writer.WriteEndObject();
}

string response = Encoding.UTF8.GetString(buffer.WrittenSpan);
Console.WriteLine(response);

// And JSON back to binary Avro.
AvroValue parsed = GenericDatumJsonReader.Create(orderSchema).Read(json);
byte[] binaryAgain = GenericDatumWriter.Create(orderSchema).WriteToArray(parsed);

// A newer version of the schema drops coupon and adds giftWrap with a default.
var orderV2 = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Order","namespace":"shop","fields":[
      {"name":"id","type":"long"},
      {"name":"customer","type":"string"},
      {"name":"status","type":{"type":"enum","name":"OrderStatus","symbols":["PENDING","SHIPPED","DELIVERED"]}},
      {"name":"note","type":["null","string"],"default":null},
      {"name":"items","type":{"type":"map","values":"int"}},
      {"name":"clientIp","type":{"type":"fixed","name":"IPv4","size":4}},
      {"name":"giftWrap","type":"boolean","default":false}]}
    """);

// JSON written with the old version: read it with that version, then resolve it through the binary encoding.
AvroValue oldValue = GenericDatumJsonReader.Create(orderSchema).Read(json);
byte[] oldBinary = GenericDatumWriter.Create(orderSchema).WriteToArray(oldValue);
GenericRecord upgraded = GenericDatumReader.Create(writerSchema: orderSchema, readerSchema: orderV2).Read(oldBinary).AsRecord();
Console.WriteLine($"Read as v2: giftWrap = {upgraded["giftWrap"].AsBoolean()}");

// JSON that is malformed or doesn't match the schema throws AvroDataException.
string wrongSymbol = json.Replace("\"SHIPPED\"", "\"LOST\"", StringComparison.Ordinal);
try
{
    GenericDatumJsonReader.Create(orderSchema).Read(wrongSymbol);
}
catch (AvroDataException ex)
{
    Console.WriteLine(ex.Message); // At $.status: 'LOST' is not a symbol of enum 'shop.OrderStatus'.
}

var ok = string.Equals(json, """{"id":1042,"customer":"Grace Hopper","status":"SHIPPED","coupon":null,"note":{"string":"Leave at the door"},"items":{"SKU-1001":2,"SKU-2040":1},"clientIp":"\u00C0\u00A8\u0001\u0014"}""", StringComparison.Ordinal)
    && string.Equals(bytesJson, "\"Hi\\u0000\\u00FF\"", StringComparison.Ordinal)
    && string.Equals(response, "{\"schema\":\"shop.Order\",\"order\":" + json + "}", StringComparison.Ordinal)
    && indented.Contains("\n  \"customer\": \"Grace Hopper\",", StringComparison.Ordinal)
    && utf8.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(json))
    && fromJson.Equals((AvroValue)order)
    && binaryAgain.AsSpan().SequenceEqual(binary)
    && !upgraded["giftWrap"].AsBoolean()
    && string.Equals(upgraded["note"].AsString(), "Leave at the door", StringComparison.Ordinal)
    && string.Equals(Message(() => GenericDatumJsonReader.Create(orderSchema).Read(wrongSymbol)), "At $.status: 'LOST' is not a symbol of enum 'shop.OrderStatus'.", StringComparison.Ordinal)
    && Message(() => GenericDatumJsonReader.Create(orderSchema).Read(json[..^1])).StartsWith("Invalid JSON: ", StringComparison.Ordinal)
    // A field missing from the JSON takes its default; a field the schema doesn't have is an error.
    && GenericDatumJsonReader.Create(orderSchema).Read(json.Replace("\"coupon\":null,", "", StringComparison.Ordinal)).Equals((AvroValue)order)
    && string.Equals(Message(() => GenericDatumJsonReader.Create(orderV2).Read(json)), "Record 'shop.Order' has no field 'coupon'.", StringComparison.Ordinal);
Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;

static string Message(Action read)
{
    try
    {
        read();
        return "";
    }
    catch (AvroDataException ex)
    {
        return ex.Message;
    }
}
