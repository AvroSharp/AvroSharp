// Writes plain.bin and zlib.bin: one record of reading.avsc in AWS Glue Schema Registry's wire format, with the header
// constants and the zlib compression of Glue's own library (software.amazon.glue:schema-registry-common), so
// AvroSharp's AwsGlue framings are checked against Glue's and not only against the documentation (#133).
// The header is written as SerializationDataEncoder.write does: version byte, compression byte, the schema version UUID
// (most then least significant bits, big-endian), then the Avro data, compressed with the zlib handler or not.
// Run from this folder: java -cp "avro-tools-1.12.2.jar;schema-registry-common-2.0.0.jar" GlueMessages.java
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.nio.ByteBuffer;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.UUID;
import com.amazonaws.services.schemaregistry.common.GlueSchemaRegistryDefaultCompression;
import com.amazonaws.services.schemaregistry.utils.AWSSchemaRegistryConstants;
import org.apache.avro.Schema;
import org.apache.avro.generic.GenericData;
import org.apache.avro.generic.GenericDatumWriter;
import org.apache.avro.generic.GenericRecord;
import org.apache.avro.io.BinaryEncoder;
import org.apache.avro.io.EncoderFactory;

public class GlueMessages {
    // The schema version ID the messages name.
    private static final UUID SCHEMA_VERSION_ID = UUID.fromString("b7b4a7f0-9b8c-4a1e-8d2f-0123456789ab");

    public static void main(String[] args) throws Exception {
        Schema schema = new Schema.Parser().parse(new File("reading.avsc"));
        GenericRecord record = new GenericData.Record(schema);
        record.put("id", 1234567890123L);
        record.put("sensor", "north-gate");
        // Repetitive data, so zlib has something to compress.
        double[] values = new double[64];
        Arrays.fill(values, 21.5);
        record.put("values", Arrays.stream(values).boxed().toList());

        ByteArrayOutputStream data = new ByteArrayOutputStream();
        BinaryEncoder encoder = EncoderFactory.get().binaryEncoder(data, null);
        new GenericDatumWriter<GenericRecord>(schema).write(record, encoder);
        encoder.flush();
        byte[] avro = data.toByteArray();

        Files.write(new File("plain.bin").toPath(), message(AWSSchemaRegistryConstants.COMPRESSION_DEFAULT_BYTE, avro));
        Files.write(new File("zlib.bin").toPath(), message(AWSSchemaRegistryConstants.COMPRESSION_BYTE, new GlueSchemaRegistryDefaultCompression().compress(avro)));
    }

    private static byte[] message(byte compression, byte[] payload) {
        ByteBuffer id = ByteBuffer.allocate(AWSSchemaRegistryConstants.SCHEMA_VERSION_ID_SIZE);
        id.putLong(SCHEMA_VERSION_ID.getMostSignificantBits()).putLong(SCHEMA_VERSION_ID.getLeastSignificantBits());
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        out.write(AWSSchemaRegistryConstants.HEADER_VERSION_BYTE);
        out.write(compression);
        out.writeBytes(id.array());
        out.writeBytes(payload);
        return out.toByteArray();
    }
}
