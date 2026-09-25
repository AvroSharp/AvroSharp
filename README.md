# AvroSharp

A high-performance .NET implementation of the [Apache Avro™](https://avro.apache.org/) specification.

> **Status:** early development. Schemas (parsing, writing, canonical form, fingerprints) work; binary encoding, container files and code generation are not implemented yet. See [the design](docs/design.md) for the roadmap.

## Goals

- Complete Avro 1.12 support: schemas, binary and JSON encoding, schema resolution, object container files, single-object encoding, canonical form and fingerprints, and all logical types.
- Faster than Apache.Avro on every scenario in the benchmark suite, with fewer allocations. This is a release gate; results will be published once the benchmarks exist.
- Serialization code produced by source generators: no reflection, Native AOT and trimming compatible.
- Async-first, low-allocation I/O over `Span<T>`, `IBufferWriter<byte>`, `ReadOnlySequence<byte>` and `System.IO.Pipelines`.
- Every codec in the specification (`null`, `deflate`, `snappy`, `bzip2`, `xz`, `zstandard`), implemented with fully managed libraries.
- Targets `net10.0`, `net9.0`, `net8.0`, `netstandard2.1` and `netstandard2.0`.

## Building

Requires the .NET 10 SDK.

```shell
dotnet build
dotnet test --solution AvroSharp.slnx
```

## License

[MIT](LICENSE).

Apache Avro, Avro and Apache are trademarks of The Apache Software Foundation. AvroSharp is an independent project and is not endorsed by or affiliated with the Apache Software Foundation.
