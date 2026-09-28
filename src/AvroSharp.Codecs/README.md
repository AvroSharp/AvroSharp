# AvroSharp.Codecs

The snappy, zstandard, bzip2 and xz codecs for [AvroSharp](https://github.com/zcsizmadia/AvroSharp)'s object container files. With the `null` and `deflate` codecs built into AvroSharp, that covers every codec in the Avro specification. All of them use fully managed libraries, with no native binaries or P/Invoke.

> **Status:** early development, not yet released.

## Reading

A file's codec is not known until it is opened, so give the reader all of them:

```csharp
using AvroSharp.Codecs;
using AvroSharp.Containers;

using var reader = AvroFileReader.OpenGeneric(stream, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
```

## Writing

| Codec | Class | Settings (defaults as in Apache Avro Java) | Library |
|---|---|---|---|
| `snappy` | `SnappyCodec.Default` | none; each block ends with a CRC-32 of its data, checked on read | [Snappier](https://github.com/brantburnett/Snappier) (BSD-3-Clause) |
| `zstandard` | `ZstandardCodec` | `level` (default 3), `checksum` (default off) | [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) (MIT) |
| `bzip2` | `Bzip2Codec` | `blockSize` 1-9 (default 9) | [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT) |
| `xz` | `XzCodec` | `level` 0-9 (default 6) | [Lzma.Net](https://github.com/zcsizmadia/Lzma.Net) (0BSD) |

```csharp
var options = new AvroFileWriterOptions { Codec = new ZstandardCodec(level: 9, checksum: true) };
using var writer = AvroFileWriter.CreateGeneric(stream, schema, options);
```

Zstandard frames without a checksum (Java's default) have no integrity check, so a damaged block may decompress to other bytes. The other codecs detect damaged blocks, and the reader reports them as `AvroDataException`.

The codecs are tested against files written by Apache Avro Java 1.12.2 in every codec, Java reads the files they write, and snappy and bzip2 are also checked against Apache.Avro C#'s codec packages in both directions.
