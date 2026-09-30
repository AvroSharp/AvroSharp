---
uid: AvroSharp.Generic
summary: *content
---
Reads and writes Avro data without generated types, as [`AvroValue`](xref:AvroSharp.Generic.AvroValue)s, with records as [`GenericRecord`](xref:AvroSharp.Generic.GenericRecord) and fixed values as [`GenericFixed`](xref:AvroSharp.Generic.GenericFixed).

- [`GenericDatumWriter`](xref:AvroSharp.Generic.GenericDatumWriter) and [`GenericDatumReader`](xref:AvroSharp.Generic.GenericDatumReader) handle the binary encoding. A reader created with a writer schema and a reader schema applies schema resolution.
- [`GenericDatumJsonWriter`](xref:AvroSharp.Generic.GenericDatumJsonWriter) and [`GenericDatumJsonReader`](xref:AvroSharp.Generic.GenericDatumJsonReader) handle the Avro JSON encoding.
- [`AvroValueTransformer`](xref:AvroSharp.Generic.AvroValueTransformer) replaces values inside record fields.

The schemas come from <xref:AvroSharp.Schemas>. The generic overloads of <xref:AvroSharp.Containers>, <xref:AvroSharp.Messages> and <xref:AvroSharp.Streams> (`CreateGeneric`, `OpenGeneric`) use these readers and writers.
