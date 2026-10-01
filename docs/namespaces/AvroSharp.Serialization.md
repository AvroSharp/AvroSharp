---
uid: AvroSharp.Serialization
summary: *content
---
Serialization of the types that the AvroSharp source generator, the `avrosharp` tool and <xref:AvroSharp.CodeGen> emit, and the attributes of the attribute-driven generator.

- [`AvroSerializer`](xref:AvroSharp.Serialization.AvroSerializer) serializes and deserializes them without delegates or reflection. It needs .NET 8 or later.
- Generated types implement [`IAvroSerializable<TSelf>`](xref:AvroSharp.Serialization.IAvroSerializable%601) (.NET 8 and later). Generated records also implement [`IAvroWritable`](xref:AvroSharp.Serialization.IAvroWritable), [`IAvroReadable`](xref:AvroSharp.Serialization.IAvroReadable) and [`IAvroSpecificRecord`](xref:AvroSharp.Serialization.IAvroSpecificRecord).
- [`AvroWriteAction<T>`](xref:AvroSharp.Serialization.AvroWriteAction%601) and [`AvroReadFunc<T>`](xref:AvroSharp.Serialization.AvroReadFunc%601) are the delegates that <xref:AvroSharp.Containers>, <xref:AvroSharp.Messages> and <xref:AvroSharp.Streams> take on every target.
- [`AvroLogicalValues`](xref:AvroSharp.Serialization.AvroLogicalValues) converts between logical types and .NET types.
- [`[AvroSerializable]`](xref:AvroSharp.Serialization.AvroSerializableAttribute) marks a `partial` class whose schema and serializers the generator builds from its members. [`AvroName`](xref:AvroSharp.Serialization.AvroNameAttribute), [`AvroAlias`](xref:AvroSharp.Serialization.AvroAliasAttribute), [`AvroDoc`](xref:AvroSharp.Serialization.AvroDocAttribute), [`AvroIgnore`](xref:AvroSharp.Serialization.AvroIgnoreAttribute), [`AvroDefault`](xref:AvroSharp.Serialization.AvroDefaultAttribute), [`AvroDecimal`](xref:AvroSharp.Serialization.AvroDecimalAttribute), [`AvroFixed`](xref:AvroSharp.Serialization.AvroFixedAttribute), [`AvroLogicalType`](xref:AvroSharp.Serialization.AvroLogicalTypeAttribute), [`AvroUnion`](xref:AvroSharp.Serialization.AvroUnionAttribute), [`AvroEnumDefault`](xref:AvroSharp.Serialization.AvroEnumDefaultAttribute), [`AvroField`](xref:AvroSharp.Serialization.AvroFieldAttribute) and [`AvroNamingPolicy`](xref:AvroSharp.Serialization.AvroNamingPolicyAttribute) adjust the schema. [Code generation](../code-generation.md#from-c-types-avroserializable) has the mapping.
- [`AvroTypes`](xref:AvroSharp.Serialization.AvroTypes) finds a type's [`AvroTypeInfo<T>`](xref:AvroSharp.Serialization.AvroTypeInfo%601), its schema and its read and write functions, by type argument or by `Type`, without reflection. Generated types register themselves, and the primitives are built in.

The members that generated code calls are in `AvroSharp.Serialization.Generated`. That namespace is for generated code, not for direct use, and may change with the generator.
