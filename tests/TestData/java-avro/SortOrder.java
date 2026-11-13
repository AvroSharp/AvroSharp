// Writes sort.avro and sort-order.txt: records of sort.avsc, and Apache Avro Java's order of every pair of them by
// BinaryData.compare, so AvroSharp's comparers are checked against Java's and not only against themselves (#175).
// Run with Java 11 or later, from this folder:  java -cp avro-tools-1.12.2.jar SortOrder.java sort.avsc sort.avro sort-order.txt
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Random;
import org.apache.avro.Schema;
import org.apache.avro.file.DataFileWriter;
import org.apache.avro.generic.GenericData;
import org.apache.avro.generic.GenericDatumWriter;
import org.apache.avro.generic.GenericRecord;
import org.apache.avro.io.BinaryData;
import org.apache.avro.io.BinaryEncoder;
import org.apache.avro.io.EncoderFactory;

public class SortOrder {
    private static final int COUNT = 250;

    // Each field changes from the base record with this probability, so many pairs tie on the leading fields and the
    // comparison reaches the later ones.
    private static final double CHANGE = 0.2;

    // Strings whose UTF-8 order differs from their UTF-16 order: U+E000 sorts before U+1F600 in UTF-8 (and in code
    // points), after it in UTF-16 code units.
    private static final String[] NAMES = {"", "a", "ab", "b", "a\u0000", "é", "", "😀", "￿"};
    private static final float[] FLOATS = {0f, -0f, 1f, -1f, Float.NaN, Float.POSITIVE_INFINITY, Float.NEGATIVE_INFINITY, Float.MIN_VALUE};
    private static final double[] DOUBLES = {0d, -0d, 1d, -1d, Double.NaN, Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY, Double.MIN_VALUE};
    private static final int[] INTS = {0, -1, 1, Integer.MIN_VALUE, Integer.MAX_VALUE};
    private static final long[] LONGS = {0L, -1L, 1L, 300L, Long.MIN_VALUE, Long.MAX_VALUE};
    private static final byte[][] BYTES = {{}, {0}, {0, 0}, {0x7f}, {(byte) 0x80}, {(byte) 0xff}};
    private static final byte[][] IDS = {{0, 0}, {0, 1}, {(byte) 0x80, 0}, {(byte) 0xff, (byte) 0xff}};

    private final Random random = new Random(175);
    private final Schema schema;
    private final Schema inner;

    private SortOrder(Schema schema) {
        this.schema = schema;
        this.inner = schema.getField("nested").schema();
    }

    public static void main(String[] args) throws Exception {
        Schema schema = new Schema.Parser().parse(new File(args[0]));
        SortOrder generator = new SortOrder(schema);
        GenericRecord base = generator.record(0);
        List<GenericRecord> records = new ArrayList<>();
        for (int i = 0; i < COUNT; i++) {
            records.add(i == 0 ? base : generator.vary(base));
        }

        GenericDatumWriter<GenericRecord> datumWriter = new GenericDatumWriter<>(schema);
        try (DataFileWriter<GenericRecord> writer = new DataFileWriter<>(datumWriter)) {
            writer.create(schema, new File(args[1]));
            for (GenericRecord record : records) {
                writer.append(record);
            }
        }

        List<byte[]> encoded = new ArrayList<>();
        for (GenericRecord record : records) {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            BinaryEncoder encoder = EncoderFactory.get().binaryEncoder(bytes, null);
            datumWriter.write(record, encoder);
            encoder.flush();
            encoded.add(bytes.toByteArray());
        }

        // One line per record: '<', '=' or '>' for its order against each record, by BinaryData.compare, which follows
        // the specification. GenericData.compare is not used: it compares bytes as signed values, and java.lang.String
        // by UTF-16 code units, where the specification compares unsigned bytes and UTF-8.
        try (PrintWriter out = new PrintWriter(new File(args[2]), StandardCharsets.US_ASCII)) {
            for (int i = 0; i < COUNT; i++) {
                StringBuilder line = new StringBuilder(COUNT);
                for (int j = 0; j < COUNT; j++) {
                    byte[] x = encoded.get(i);
                    byte[] y = encoded.get(j);
                    int binary = Integer.signum(BinaryData.compare(x, 0, y, 0, schema));
                    line.append(binary < 0 ? '<' : binary > 0 ? '>' : '=');
                }

                out.print(line);
                out.print('\n');
            }
        }
    }

    private GenericRecord vary(GenericRecord base) {
        GenericRecord record = record(1);
        for (Schema.Field field : schema.getFields()) {
            if (random.nextDouble() >= CHANGE) {
                record.put(field.pos(), base.get(field.pos()));
            }
        }

        return record;
    }

    // A record with every field from its set: the first value of each when first is 0, random values otherwise.
    private GenericRecord record(int first) {
        GenericRecord record = new GenericData.Record(schema);
        record.put("flag", first == 0 ? false : random.nextBoolean());
        record.put("small", INTS[pick(INTS.length, first)]);
        Map<String, String> tags = new HashMap<>();
        for (int i = random.nextInt(3); i > 0; i--) {
            tags.put("k" + random.nextInt(5), "v" + random.nextInt(5));
        }

        record.put("tags", tags);
        record.put("kind", new GenericData.EnumSymbol(schema.getField("kind").schema(), schema.getField("kind").schema().getEnumSymbols().get(pick(3, first))));
        record.put("name", NAMES[pick(NAMES.length, first)]);
        record.put("count", LONGS[pick(LONGS.length, first)]);
        record.put("ratio", FLOATS[pick(FLOATS.length, first)]);
        record.put("score", DOUBLES[pick(DOUBLES.length, first)]);
        record.put("data", ByteBuffer.wrap(BYTES[pick(BYTES.length, first)]));
        record.put("id", new GenericData.Fixed(schema.getField("id").schema(), IDS[pick(IDS.length, first)]));
        switch (pick(4, first)) {
            case 0:
                record.put("choice", null);
                break;
            case 1:
                record.put("choice", INTS[random.nextInt(3)]);
                break;
            case 2:
                record.put("choice", NAMES[random.nextInt(3)]);
                break;
            default:
                record.put("choice", inner(first));
                break;
        }

        List<Integer> list = new ArrayList<>();
        for (int i = pick(4, first); i > 0; i--) {
            list.add(random.nextInt(2));
        }

        record.put("list", list);
        record.put("nested", inner(first));
        record.put("note", "n" + random.nextInt(1000));
        return record;
    }

    private GenericRecord inner(int first) {
        GenericRecord record = new GenericData.Record(inner);
        record.put("a", pick(2, first));
        record.put("b", NAMES[pick(3, first)]);
        record.put("skip", random.nextDouble());
        return record;
    }

    private int pick(int count, int first) {
        return first == 0 ? 0 : random.nextInt(count);
    }
}
