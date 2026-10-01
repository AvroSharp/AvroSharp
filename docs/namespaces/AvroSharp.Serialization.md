---
uid: AvroSharp.Serialization
summary: *content
---
Serialization of the types that the AvroSharp source generator, the `avrosharp` tool and <xref:AvroSharp.CodeGen> emit.

- [`AvroSerializer`](xref:AvroSharp.Serialization.AvroSerializer) serializes and deserializes them without delegates or reflection. It needs .NET 8 or later.
- Generated types implement [`IAvroSerializable<TSelf>`](xref:AvroSharp.Serialization.IAvroSerializable%601) (.NET 8 and later). Generated records also implement [`IAvroWritable`](xref:AvroSharp.Serialization.IAvroWritable), [`IAvroReadable`](xref:AvroSharp.Serialization.IAvroReadable) and [`IAvroSpecificRecord`](xref:AvroSharp.Serialization.IAvroSpecificRecord).
- [`AvroWriteAction<T>`](xref:AvroSharp.Serialization.AvroWriteAction%601) and [`AvroReadFunc<T>`](xref:AvroSharp.Serialization.AvroReadFunc%601) are the delegates that <xref:AvroSharp.Containers>, <xref:AvroSharp.Messages> and <xref:AvroSharp.Streams> take on every target.
- [`AvroLogicalValues`](xref:AvroSharp.Serialization.AvroLogicalValues) converts between logical types and .NET types.

The members that generated code calls are in `AvroSharp.Serialization.Generated`. That namespace is for generated code, not for direct use, and may change with the generator.
