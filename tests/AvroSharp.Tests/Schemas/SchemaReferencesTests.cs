using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>
/// Schemas with references to named types of other schemas, as schema registries store them (#80), checked against
/// what Apache Avro Java 1.12 prints for the same files (tests/TestData/schema-references, written by Generate.java).
/// </summary>
public class SchemaReferencesTests
{
    private static readonly string[] s_files = ["address", "customer", "order"];

    [Test]
    [Arguments("address")]
    [Arguments("customer")]
    [Arguments("order")]
    public async Task ToJson_IsTheTextJavaPrints(string name)
    {
        var schema = ParseInOrder()[name];

        await Assert.That(schema.ToJson()).IsEqualTo(Read(name + ".java-inlined.json"));
    }

    [Test]
    [Arguments("address")]
    [Arguments("customer")]
    [Arguments("order")]
    public async Task ToJsonWithReferences_IsTheTextJavaPrintsForARegistry(string name)
    {
        var schemas = ParseInOrder();
        // As in Confluent's registry: the schemas of the referenced subjects, not the types nested in them.
        var referenced = s_files.TakeWhile(f => !string.Equals(f, name, StringComparison.Ordinal)).Select(f => (NamedSchema)schemas[f]).ToList();

        await Assert.That(schemas[name].ToJson(referenced)).IsEqualTo(Read(name + ".java-referenced.json"));
    }

    [Test]
    public async Task ASchemaWithReferences_ParsesAgainstThePreParsedTypes_AndRoundTrips()
    {
        // As a registry client resolves references: parse each referenced schema, then the schema itself.
        var parser = new AvroSchemaParser();
        parser.Parse(Read("address.java-referenced.json"));
        parser.Parse(Read("customer.java-referenced.json"));
        var order = parser.Parse(Read("order.java-referenced.json"));

        var inlined = new AvroSchemaParser().Parse(Read("order.java-inlined.json"));

        await Assert.That(order.CanonicalForm).IsEqualTo(inlined.CanonicalForm);
        await Assert.That(order.ToJson()).IsEqualTo(Read("order.java-inlined.json"));
    }

    [Test]
    public async Task AMissingReference_IsNamedInTheError()
    {
        var parser = new AvroSchemaParser();
        parser.Parse(Read("address.java-referenced.json"));

        var ex = Assert.Throws<AvroSchemaException>(() => parser.Parse(Read("order.java-referenced.json")));

        await Assert.That(ex.Message).Contains("com.acme.crm.Customer");
    }

    [Test]
    public async Task AReferencedRoot_IsWrittenByName()
    {
        var schemas = ParseInOrder();
        var address = (NamedSchema)schemas["address"];

        await Assert.That(address.ToJson([address])).IsEqualTo("\"com.acme.common.Address\"");
    }

    [Test]
    public async Task Numbers_ArePrintedAsJacksonPrintsThem()
    {
        var mismatches = new List<string>();
        var lines = File.ReadAllLines(TestData.PathOf("schema-references/numbers.java.txt")).Where(l => l.Length > 0).ToList();
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            var ours = JavaJsonNumbers.Format(parts[0]);
            if (!string.Equals(ours, parts[1], StringComparison.Ordinal))
            {
                mismatches.Add($"{parts[0]}: {ours} (Java {parts[1]})");
            }
        }

        await Assert.That(lines.Count).IsGreaterThan(300);
        await Assert.That(string.Join("\n", mismatches)).IsEqualTo(string.Empty);
    }

    private static Dictionary<string, AvroSchema> ParseInOrder()
    {
        var parser = new AvroSchemaParser();
        return s_files.ToDictionary(f => f, f => parser.Parse(Read(f + ".avsc")), StringComparer.Ordinal);
    }

    private static string Read(string file) => File.ReadAllText(TestData.PathOf("schema-references/" + file));
}
