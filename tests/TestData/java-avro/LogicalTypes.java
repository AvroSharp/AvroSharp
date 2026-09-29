// Writes logical.avro: logical.avsc's edge values, through Apache Avro Java's own logical-type conversions, so
// AvroSharp's encodings are checked against Java's and not only against themselves (#133).
// Run with Java 11 or later, from this folder:  java -cp avro-tools-1.12.2.jar LogicalTypes.java logical.avsc logical.avro
import java.io.File;
import java.math.BigDecimal;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.LocalTime;
import java.util.UUID;
import org.apache.avro.Conversions;
import org.apache.avro.Schema;
import org.apache.avro.data.TimeConversions;
import org.apache.avro.file.DataFileWriter;
import org.apache.avro.generic.GenericData;
import org.apache.avro.generic.GenericDatumWriter;
import org.apache.avro.generic.GenericRecord;

public class LogicalTypes {
    public static void main(String[] args) throws Exception {
        Schema schema = new Schema.Parser().parse(new File(args[0]));
        GenericData model = new GenericData();
        model.addLogicalTypeConversion(new Conversions.DecimalConversion());
        model.addLogicalTypeConversion(new Conversions.UUIDConversion());
        model.addLogicalTypeConversion(new TimeConversions.DateConversion());
        model.addLogicalTypeConversion(new TimeConversions.TimeMillisConversion());
        model.addLogicalTypeConversion(new TimeConversions.TimeMicrosConversion());
        model.addLogicalTypeConversion(new TimeConversions.TimestampMillisConversion());
        model.addLogicalTypeConversion(new TimeConversions.TimestampMicrosConversion());
        model.addLogicalTypeConversion(new TimeConversions.TimestampNanosConversion());
        model.addLogicalTypeConversion(new TimeConversions.LocalTimestampMillisConversion());
        model.addLogicalTypeConversion(new TimeConversions.LocalTimestampMicrosConversion());
        model.addLogicalTypeConversion(new TimeConversions.LocalTimestampNanosConversion());

        try (DataFileWriter<GenericRecord> writer = new DataFileWriter<>(new GenericDatumWriter<GenericRecord>(schema, model))) {
            writer.create(schema, new File(args[1]));

            // Before 1970, and a negative decimal.
            writer.append(row(schema, "-12345.6789", "00112233-4455-6677-8899-aabbccddeeff", LocalDate.of(1969, 12, 31),
                LocalTime.of(23, 59, 59, 999_999_000), Instant.parse("1969-12-31T23:59:59.999999999Z"), 1, 2, 3));
            // Unscaled -128: one byte, 0x80, the smallest negative first byte.
            writer.append(row(schema, "-0.0128", "ffffffff-ffff-ffff-ffff-ffffffffffff", LocalDate.of(2038, 1, 19),
                LocalTime.of(0, 0), Instant.parse("2038-01-19T03:14:08.000000001Z"), 0, 0, 0));
            // Unscaled +128: two bytes, 0x00 0x80.
            writer.append(row(schema, "0.0128", "00000000-0000-0000-0000-000000000001", LocalDate.of(1970, 1, 1),
                LocalTime.of(12, 34, 56, 789_012_000), Instant.EPOCH, 12, 31, 86_399_999));
            // The largest and smallest values of precision 20.
            writer.append(row(schema, "9999999999999999.9999", "123e4567-e89b-12d3-a456-426614174000", LocalDate.of(1, 1, 1),
                LocalTime.of(23, 59, 59, 999_000_000), Instant.parse("1900-01-01T00:00:00.123456789Z"), Integer.MAX_VALUE, 0, 1));
            writer.append(row(schema, "-9999999999999999.9999", "a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4", LocalDate.of(9999, 12, 31),
                LocalTime.of(1, 2, 3, 4_000_000), Instant.parse("2262-04-11T23:47:16.854775807Z"), 0, Integer.MAX_VALUE, 0));
        }
    }

    private static GenericRecord row(Schema schema, String decimal, String uuid, LocalDate day, LocalTime time, Instant at, int months, int days, int millis) {
        GenericRecord record = new GenericData.Record(schema);
        BigDecimal value = new BigDecimal(decimal);
        record.put("decimal_bytes", value);
        record.put("decimal_fixed", value);
        record.put("uuid_string", UUID.fromString(uuid));
        record.put("uuid_fixed", UUID.fromString(uuid));
        record.put("day", day);
        record.put("time_millis", time.withNano(time.getNano() / 1_000_000 * 1_000_000));
        record.put("time_micros", time);
        record.put("at_millis", at);
        record.put("at_micros", at);
        record.put("at_nanos", at);
        record.put("local_millis", LocalDateTime.ofInstant(at, java.time.ZoneOffset.UTC));
        record.put("local_micros", LocalDateTime.ofInstant(at, java.time.ZoneOffset.UTC));
        record.put("local_nanos", LocalDateTime.ofInstant(at, java.time.ZoneOffset.UTC));

        // Java has no duration conversion: the specification's three little-endian unsigned ints.
        ByteBuffer span = ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN).putInt(months).putInt(days).putInt(millis);
        record.put("span", new GenericData.Fixed(schema.getField("span").schema(), span.array()));
        return record;
    }
}
