---
uid: AvroSharp.Streams
summary: *content
---
Objects written one after another to a stream in the binary encoding, with no container or framing, so the reader must be given the writer schema. [`AvroStreamWriter`](xref:AvroSharp.Streams.AvroStreamWriter) creates an [`AvroStreamWriter<T>`](xref:AvroSharp.Streams.AvroStreamWriter%601), and [`AvroStreamReader`](xref:AvroSharp.Streams.AvroStreamReader) opens an [`AvroStreamReader<T>`](xref:AvroSharp.Streams.AvroStreamReader%601); both have sync and async members. [`AvroStreamOptions`](xref:AvroSharp.Streams.AvroStreamOptions) sets the buffer size and the largest object a reader accepts. They work with generated types (<xref:AvroSharp.Serialization>) and, through `CreateGeneric` and `OpenGeneric`, with the generic values of <xref:AvroSharp.Generic>. For files that carry their schema, use <xref:AvroSharp.Containers>.
