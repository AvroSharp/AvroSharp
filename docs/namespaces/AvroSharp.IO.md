---
uid: AvroSharp.IO
summary: *content
---
The Avro binary encoding at the lowest level. [`AvroWriter`](xref:AvroSharp.IO.AvroWriter) writes into an `IBufferWriter<byte>` or a fixed `Span<byte>` without intermediate buffers, and [`AvroReader`](xref:AvroSharp.IO.AvroReader) reads from a `ReadOnlySpan<byte>` or a `ReadOnlySequence<byte>`. Both work one value at a time and know nothing of schemas. The generic readers and writers of <xref:AvroSharp.Generic> and the serializers of generated types (<xref:AvroSharp.Serialization>) are built on them. Use them directly to write a serializer by hand.
