# Apache Avro Java reference files

Object container files written by Apache Avro Java 1.12.2 (`avro-tools`), one per codec, so AvroSharp's codecs are checked against Java's output and not only against themselves. They are used only by the tests and are not part of any AvroSharp package.

| Files | Contents |
|---|---|
| `weather-<codec>.avro` | Apache's `../apache-avro/weather.avro` (five records, one block), recompressed with `avro-tools recodec` |
| `many-<codec>.avro` | 8000 generated records of `many.avsc` (several blocks), written with `avro-tools fromjson` |

The codecs are `deflate`, `snappy`, `bzip2`, `xz` and `zstandard`, at Java's default levels. `weather.avro` is licensed under the Apache License 2.0 (see `../apache-avro/LICENSE.txt`), and so are these derived files.

To regenerate them, run `./generate.ps1 -Java <java> -AvroTools <avro-tools-1.12.2.jar>`. The jar is at https://repo1.maven.org/maven2/org/apache/avro/avro-tools/1.12.2/.
