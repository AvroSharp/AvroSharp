# AWS Glue Schema Registry reference messages

One record of `reading.avsc` in the AWS Glue Schema Registry wire format, written by `GlueMessages.java` with Glue's own library, [software.amazon.glue:schema-registry-common](https://central.sonatype.com/artifact/software.amazon.glue/schema-registry-common) 2.0.0 (Apache License 2.0). AvroSharp's `AwsGlue` and `AwsGlueCompressed` framings are checked against them, not only against the documentation.

| File | Contents |
|---|---|
| `plain.bin` | Header version `0x03`, compression byte `0x00`, the schema version UUID (`b7b4a7f0-9b8c-4a1e-8d2f-0123456789ab`, big-endian), and the Avro data |
| `zlib.bin` | The same with compression byte `0x05`, and the data compressed by Glue's `GlueSchemaRegistryDefaultCompression` (zlib) |

The header constants are Glue's (`AWSSchemaRegistryConstants`), written in the order its `SerializationDataEncoder.write` uses; running the encoder itself would need the AWS SDK.

To regenerate them, run from this folder, with Java 11 or later: `java -cp "avro-tools-1.12.2.jar;schema-registry-common-2.0.0.jar" GlueMessages.java` (`:` instead of `;` on Linux and macOS). The jars are at https://repo1.maven.org/maven2/org/apache/avro/avro-tools/1.12.2/ and https://repo1.maven.org/maven2/software/amazon/glue/schema-registry-common/2.0.0/.
