# API reference

The public API of the AvroSharp packages, generated from the XML documentation in the source.

| Namespace | What it holds |
|---|---|
| `AvroSharp` | `AvroException` and `AvroCodecNames`, the codec names of the specification |
| `AvroSharp.Schemas` | The schema model and parser, Parsing Canonical Form, fingerprints, logical types |
| `AvroSharp.IO` | `AvroWriter` and `AvroReader`: the binary encoding over spans, buffer writers and sequences |
| `AvroSharp.Generic` | `AvroValue`, `GenericRecord`, and the generic binary and JSON readers and writers, with schema resolution |
| `AvroSharp.Containers` | Object container files, sync and async, with seeking and pipelined reading, and `AvroCodec` |
| `AvroSharp.Codecs` | The snappy, zstandard, bzip2 and xz codecs (the `AvroSharp.Codecs` package) |
| `AvroSharp.Messages` | Single-object encoding and schema-registry framing |
| `AvroSharp.Streams` | Streams of objects without a container |
| `AvroSharp.Serialization` | The support that generated code calls, and the delegates it plugs into |
| `AvroSharp.CodeGen` | The C# code generation engine (the `AvroSharp.CodeGen` package) |

## .NET 8 and later only

The reference is built from the `net10.0` build, so it shows every member. These need .NET 8 or later, because they rely on static abstract interface members; on .NET Standard and .NET Framework, the overloads that take a schema and the generated `Write`/`Read` delegates do the same:

| Member | On every target |
|---|---|
| `IAvroSerializable<TSelf>`, which generated types implement with C# 11 or later | the generated static `Schema`, `Write` and `Read` |
| `AvroSerializer.Serialize<T>`, `TrySerialize<T>`, `Deserialize<T>` | `value.ToAvroBytes()`, `T.FromAvroBytes(bytes)` |
| `AvroFileWriter.Create<T>(stream)`, `AvroFileReader.Open<T>(stream)`, `OpenAsync<T>(stream)` | `AvroFileWriter.Create<T>(stream, T.Schema, T.Write)`, `AvroFileReader.Open<T>(stream, _ => T.Read)` |
| `AvroStreamWriter.Create<T>(stream)`, `AvroStreamReader.Open<T>(stream)` | the overloads with `T.Write` and `T.Read` |
| `AvroMessage.ToArray<T>`/`Write<T>`, `AvroMessageReader.Create<T>` | the overloads with the schema and `T.Write`, or a `createReader` delegate |
| `AvroRegistryMessage.ToArray<T>`/`Write<T>`, `AvroRegistryMessageReader.Create<T>` | the overloads with `T.Write`, or a `createReader` delegate |
