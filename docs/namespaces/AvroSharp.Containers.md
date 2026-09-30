---
uid: AvroSharp.Containers
summary: *content
---
Avro object container files, sync and async. [`AvroFileWriter`](xref:AvroSharp.Containers.AvroFileWriter) creates writers and [`AvroFileReader`](xref:AvroSharp.Containers.AvroFileReader) opens readers, for generated types or, through `CreateGeneric` and `OpenGeneric`, for the generic values of <xref:AvroSharp.Generic>. A reader, [`AvroFileReader<T>`](xref:AvroSharp.Containers.AvroFileReader%601), can seek to sync markers and read blocks in a pipeline.

- [`AvroFileWriterOptions`](xref:AvroSharp.Containers.AvroFileWriterOptions) sets the codec, the sync interval and the metadata. [`AvroFileReaderOptions`](xref:AvroSharp.Containers.AvroFileReaderOptions) sets the codecs a reader accepts and its limits.
- [`AvroCodec`](xref:AvroSharp.Containers.AvroCodec) is the base class of the block codecs. This namespace has the null codec and [`DeflateCodec`](xref:AvroSharp.Containers.DeflateCodec). [`AvroCodecNames`](xref:AvroSharp.Containers.AvroCodecNames) holds the codec names of the specification.

The other codecs are in <xref:AvroSharp.Codecs>, in the `AvroSharp.Codecs` package.
