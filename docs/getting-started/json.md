# JSON

Avro defines a JSON encoding next to its binary one. This page writes a generic record as Avro JSON, reads it back, shows where the encoding differs from the JSON you might write by hand, and converts binary Avro to JSON and back.

This is the Avro JSON encoding from the specification, the one Java's `JsonEncoder` and `JsonDecoder` (and Apache.Avro C#'s) read and write, so the text is exchanged with other Avro libraries. It is not a general JSON serializer for .NET objects: every value is written and read against an Avro schema. For JSON of arbitrary objects, use `System.Text.Json` itself.

## Write a value as JSON

The examples use one record schema, `shop.Order`, with an enum, two optional strings, a map of quantities and a 4-byte fixed:

```csharp
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
```

[`GenericDatumJsonWriter.Create`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.Create.html) returns a writer for a schema, cached and thread-safe like the binary [`GenericDatumWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumWriter.html). [`WriteToString`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.WriteToString.html) returns the text. The sample builds `order`, a [`GenericRecord`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.html) of this schema:

```csharp
var orderJson = GenericDatumJsonWriter.Create(orderSchema);
string json = orderJson.WriteToString(order);
Console.WriteLine(json);
```

```text
{"id":1042,"customer":"Grace Hopper","status":"SHIPPED","coupon":null,"note":{"string":"Leave at the door"},"items":{"SKU-1001":2,"SKU-2040":1},"clientIp":"\u00C0\u00A8\u0001\u0014"}
```

The only formatting option is indentation. [`WriteToUtf8Bytes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.WriteToUtf8Bytes.html) gives the same JSON as UTF-8, and [`Write`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonWriter.Write.html) writes to a `Utf8JsonWriter`, which can write to a stream and has its own options:

```csharp
string indented = orderJson.WriteToString(order, indented: true);
byte[] utf8 = orderJson.WriteToUtf8Bytes(order);
```

## Read it back

[`GenericDatumJsonReader.Create`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonReader.Create.html) takes the schema the JSON was written with, and [`Read`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumJsonReader.Read.html) returns an [`AvroValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.AvroValue.html). It also reads UTF-8 bytes, or the next value from a `Utf8JsonReader`:

```csharp
AvroValue fromJson = GenericDatumJsonReader.Create(orderSchema).Read(json);
GenericRecord copy = fromJson.AsRecord();
Console.WriteLine($"Read back: {copy["customer"].AsString()}, {copy["status"].AsEnumSymbol()}");
```

Record fields may come in any order. A field missing from the JSON takes its default value; a missing field without a default, an unknown field or a repeated one is an error.

## How it differs from plain JSON

The encoding is JSON, but the schema decides how each value looks. In the output above:

- **Unions:** a value other than `null` is wrapped in an object whose one property names its branch: `"note":{"string":"Leave at the door"}`. A named type's branch is its full name, such as `{"shop.Order":{...}}`. `null` is not wrapped: `"coupon":null`.
- **Bytes and fixed:** a string whose code points 0-255 are the bytes. The address 192.168.1.20 is `"\u00C0\u00A8\u0001\u0014"`: the JSON writer escapes the code points outside printable ASCII, so the text stays ASCII.
- **Enums:** the symbol as a string: `"status":"SHIPPED"`.
- **Maps:** an object, one property per key: `"items":{"SKU-1001":2,"SKU-2040":1}`.

The same rule for `bytes`, written on its own:

```csharp
string bytesJson = GenericDatumJsonWriter.Create(AvroSchema.Parse("\"bytes\"")).WriteToString(new byte[] { 0x48, 0x69, 0x00, 0xFF });
Console.WriteLine(bytesJson);
```

```text
"Hi\u0000\u00FF"
```

Records are objects and arrays are arrays, as in plain JSON. `float` and `double` NaN and infinities, which JSON numbers can't express, are the strings `"NaN"`, `"Infinity"` and `"-Infinity"`.

## Convert binary Avro to JSON and back

To inspect binary data, or to return it from a REST API, read it with [`GenericDatumReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.html) and write the value with the JSON writer. `Write` puts it inside a larger document:

```csharp
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
```

```text
{"schema":"shop.Order","order":{"id":1042,"customer":"Grace Hopper","status":"SHIPPED","coupon":null,"note":{"string":"Leave at the door"},"items":{"SKU-1001":2,"SKU-2040":1},"clientIp":"\u00C0\u00A8\u0001\u0014"}}
```

The other way, read the JSON and write the value in the binary encoding. The sample checks that the bytes are the same as the original's:

```csharp
AvroValue parsed = GenericDatumJsonReader.Create(orderSchema).Read(json);
byte[] binaryAgain = GenericDatumWriter.Create(orderSchema).WriteToArray(parsed);
```

## Read JSON written with an older schema

The JSON reader takes one schema, the one the JSON was written with; there is no reader-schema overload. Its defaults for missing fields cover a newer schema that only adds fields with defaults. But a field the newer schema dropped is an error (`Record 'shop.Order' has no field 'coupon'.`), and no types are promoted.

For full [schema resolution](schema-evolution.md), read the JSON with the old version and resolve it through the binary encoding. Here version 2 drops `coupon` and adds `giftWrap` with the default `false`:

```csharp
AvroValue oldValue = GenericDatumJsonReader.Create(orderSchema).Read(json);
byte[] oldBinary = GenericDatumWriter.Create(orderSchema).WriteToArray(oldValue);
GenericRecord upgraded = GenericDatumReader.Create(writerSchema: orderSchema, readerSchema: orderV2).Read(oldBinary).AsRecord();
```

## Errors

JSON that is malformed or doesn't match the schema throws [`AvroDataException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroDataException.html). The message gives the JSON path of the value:

```csharp
string wrongSymbol = json.Replace("\"SHIPPED\"", "\"LOST\"", StringComparison.Ordinal);
try
{
    GenericDatumJsonReader.Create(orderSchema).Read(wrongSymbol);
}
catch (AvroDataException ex)
{
    Console.WriteLine(ex.Message); // At $.status: 'LOST' is not a symbol of enum 'shop.OrderStatus'.
}
```

For malformed JSON, the message starts with `Invalid JSON:` and gives the parser's line and position. The writer throws [`AvroException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroException.html) when a value doesn't match its schema.

The whole program is the [Json sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/Json).

## Next

- [Getting started](index.md), [container files](containers.md), [schema evolution](schema-evolution.md) and [logical types](logical-types.md).
- The [`AvroSharp.Generic` namespace](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.html) in the API reference, and [migrating from Apache.Avro](../migrating-from-apache-avro.md).
