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
