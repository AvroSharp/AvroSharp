import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import org.apache.avro.Schema;

/**
 * Writes what Apache Avro Java prints for the schemas in this folder: toString() of each schema on its own
 * (references inlined) and toString(referencedSchemas, false) of each schema parsed against the ones before it,
 * with the earlier schemas as the referenced ones, which is the text Confluent's registry stores for a schema
 * whose references are those subjects.
 */
public class Generate {
  public static void main(String[] args) throws Exception {
    Path dir = Path.of(args[0]);
    // JSON numbers as Jackson prints them after parsing, which is how Avro writes defaults and properties: the
    // hand-picked inputs in numbers.txt, then random doubles (seeded) given as Java's own shortest text.
    com.fasterxml.jackson.databind.ObjectMapper mapper = new com.fasterxml.jackson.databind.ObjectMapper();
    List<String> numbers = new ArrayList<>();
    for (String input : Files.readAllLines(dir.resolve("numbers.txt"))) {
      if (!input.isEmpty()) numbers.add(input + " " + mapper.readTree(input).toString());
    }
    java.util.Random random = new java.util.Random(20260927);
    while (numbers.size() < 334) {
      double d = Double.longBitsToDouble(random.nextLong());
      if (Double.isFinite(d)) numbers.add(Double.toString(d) + " " + mapper.readTree(Double.toString(d)).toString());
    }
    Files.write(dir.resolve("numbers.java.txt"), numbers, StandardCharsets.UTF_8);

    String[] files = {"address", "customer", "order"};
    Schema.Parser parser = new Schema.Parser();
    List<Schema> referenced = new ArrayList<>();
    for (String name : files) {
      Schema schema = parser.parse(new File(dir.toFile(), name + ".avsc"));
      Files.writeString(dir.resolve(name + ".java-inlined.json"), schema.toString(), StandardCharsets.UTF_8);
      Files.writeString(dir.resolve(name + ".java-referenced.json"), schema.toString(referenced, false), StandardCharsets.UTF_8);
      // Like Confluent's registry: the schemas of the referenced subjects are known by name, the types nested in
      // them are not (a registry stores such types inline, so reference them through subjects of their own).
      referenced.add(schema);
    }
  }
}
