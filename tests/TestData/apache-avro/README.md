# Apache Avro test data

Files in this folder are copied unchanged from the [Apache Avro](https://github.com/apache/avro) repository and are licensed under the Apache License 2.0 (`LICENSE.txt`, `NOTICE.txt`). They are used only by AvroSharp's tests and are not part of any AvroSharp package.

| File | Source path |
|---|---|
| `schema-tests.txt` | `share/test/data/schema-tests.txt` |
| `weather.avro`, `weather-sorted.avro`, `weather-snappy.avro`, `weather.json` | `share/test/data/` |
| `syncInMeta.avro` | `share/test/data/syncInMeta.avro` |
| `messageV1/*` | `share/test/data/messageV1/` |
| `LICENSE.txt`, `NOTICE.txt` | repository root |

Source: tag `release-1.12.2`, commit `8fa2067f70e3012cb3fd9a8839cd97e8c7cc1772`.

To refresh, run `./refresh.ps1 -Commit <sha>` and update the commit above.
