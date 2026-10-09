# AvroSharp.Codecs

The snappy, zstandard, bzip2 and xz codecs for [AvroSharp](https://github.com/AvroSharp/AvroSharp)'s object container files, in the [`AvroSharp.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.html) namespace. With the `null` and `deflate` codecs built into AvroSharp, that covers every codec in the Avro specification. All of them use fully managed libraries, with no native binaries or P/Invoke.

> **Status:** stable. The public API follows [semantic versioning](https://semver.org/): no breaking changes before 2.0.

**[Documentation](https://avrosharp.github.io/AvroSharp/)** · [Container files and codecs](https://github.com/AvroSharp/AvroSharp#object-container-files) · [Benchmarks by codec](https://avrosharp.github.io/AvroSharp/docs/benchmarks.html)

## Reading

A file's codec is not known until it is opened, so give the reader all of them: [`AvroCodecs.All`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.AvroCodecs.All.html) as [`AvroFileReaderOptions.Codecs`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReaderOptions.Codecs.html):

```csharp
using AvroSharp.Codecs;
using AvroSharp.Containers;

using var reader = AvroFileReader.OpenGeneric(stream, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
```

## Writing

| Codec | Class | Settings (defaults as in Apache Avro Java) | Library |
|---|---|---|---|
| `snappy` | [`SnappyCodec.Default`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.SnappyCodec.Default.html) | none; each block ends with a CRC-32 of its data, checked on read | [Snappiest](https://github.com/zcsizmadia/Snappiest) on .NET 8+, [Snappier](https://github.com/brantburnett/Snappier) on .NET Standard (both BSD-3-Clause) |
| `zstandard` | [`ZstandardCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.ZstandardCodec.html) | `level` (default 3), `checksum` (default off) | [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) (MIT) |
| `bzip2` | [`Bzip2Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.Bzip2Codec.html) | `blockSize` 1-9 (default 9) | [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT) |
| `xz` | [`XzCodec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Codecs.XzCodec.html) | `level` 0-9 (default 6) | [Lzma.Net](https://github.com/zcsizmadia/Lzma.Net) (0BSD) |

A writer takes one as [`AvroFileWriterOptions.Codec`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileWriterOptions.Codec.html):

```csharp
var options = new AvroFileWriterOptions { Codec = new ZstandardCodec(level: 9, checksum: true) };
using var writer = AvroFileWriter.CreateGeneric(stream, schema, options);
```

Zstandard frames without a checksum (Java's default) have no integrity check, so a damaged block may decompress to other bytes. The other codecs detect damaged blocks, and the reader reports them as [`AvroDataException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroDataException.html).

The codecs are tested against files written by Apache Avro Java 1.12.2 in every codec, Java reads the files they write, and snappy and bzip2 are also checked against Apache.Avro C#'s codec packages in both directions.
