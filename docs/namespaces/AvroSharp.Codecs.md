---
uid: AvroSharp.Codecs
summary: *content
---
The codecs of the `AvroSharp.Codecs` package: [`SnappyCodec`](xref:AvroSharp.Codecs.SnappyCodec), [`ZstandardCodec`](xref:AvroSharp.Codecs.ZstandardCodec), [`Bzip2Codec`](xref:AvroSharp.Codecs.Bzip2Codec) and [`XzCodec`](xref:AvroSharp.Codecs.XzCodec). Each is an [`AvroCodec`](xref:AvroSharp.Containers.AvroCodec); give one to a writer through [`AvroFileWriterOptions.Codec`](xref:AvroSharp.Containers.AvroFileWriterOptions.Codec%2A). [`AvroCodecs.All`](xref:AvroSharp.Codecs.AvroCodecs.All%2A) holds all four with their default settings. Given to [`AvroFileReaderOptions.Codecs`](xref:AvroSharp.Containers.AvroFileReaderOptions.Codecs%2A), it lets a reader read every codec in the specification, with the built-in null and deflate codecs. The files themselves are read and written with <xref:AvroSharp.Containers>.
