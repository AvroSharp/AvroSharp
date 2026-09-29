using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

public class SchemaWriterTests
{
    private const string Complex = """
        {"type":"record","name":"Order","namespace":"shop","doc":"An order","aliases":["Purchase"],"meta":{"owner":"team"},"fields":[
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"total","type":{"type":"bytes","logicalType":"decimal","precision":12,"scale":2},"default":"\u0000"},
          {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID"],"default":"NEW"},"order":"descending"},
          {"name":"lines","type":{"type":"array","items":{"type":"record","name":"Line","fields":[
            {"name":"sku","type":{"type":"fixed","name":"Sku","namespace":"catalog","size":8}},
            {"name":"qty","type":"int","default":1}
          ]}}},
          {"name":"next","type":["null","Order"],"default":null},
          {"name":"skus","type":{"type":"map","values":"catalog.Sku"},"aliases":["codes"]},
          {"name":"root","type":{"type":"record","name":"Root","namespace":"","fields":[{"name":"s","type":"shop.Status"}]}}
        ]}
        """;

    [Test]
    public async Task ToJson_RoundTripsEveryAttribute()
    {
        var schema = AvroSchema.Parse(Complex);
        var json = schema.ToJson();
        var reparsed = (RecordSchema)AvroSchema.Parse(json);

        await Assert.That(reparsed.ToJson()).IsEqualTo(json);
        await Assert.That(reparsed.CanonicalForm).IsEqualTo(schema.CanonicalForm);
        await Assert.That(reparsed.Doc).IsEqualTo("An order");
        await Assert.That(reparsed.Aliases[0].FullName).IsEqualTo("shop.Purchase");
        await Assert.That(reparsed.Properties["meta"].GetProperty("owner").GetString()).IsEqualTo("team");
        await Assert.That(reparsed.GetField("status").Order).IsEqualTo(FieldOrder.Descending);
        await Assert.That(((EnumSchema)reparsed.GetField("status").Schema).Default).IsEqualTo("NEW");
        await Assert.That(reparsed.GetField("skus").Aliases[0]).IsEqualTo("codes");
        await Assert.That(((NamedSchema)reparsed.GetField("root").Schema).FullName).IsEqualTo("Root");
    }

    [Test]
    public async Task ToJson_DefinesEachNamedTypeOnceAndUsesTheShortestName()
    {
        var json = AvroSchema.Parse(Complex).ToJson();
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.GetProperty("fields");

        // "Order" is referenced by its simple name inside its own namespace.
        await Assert.That(fields[4].GetProperty("type")[1].GetString()).IsEqualTo("Order");

        // "catalog.Sku" is outside the enclosing namespace, so its full name is used.
        await Assert.That(fields[5].GetProperty("type").GetProperty("values").GetString()).IsEqualTo("catalog.Sku");

        // "Root" moves to the null namespace, which must be written explicitly.
        await Assert.That(fields[6].GetProperty("type").GetProperty("namespace").GetString()).IsEqualTo(string.Empty);
        await Assert.That(fields[6].GetProperty("type").GetProperty("fields")[0].GetProperty("type").GetString()).IsEqualTo("shop.Status");
    }

    [Test]
    public async Task CanonicalForm_UsesFullNamesAndStripsEverythingElse()
    {
        var schema = AvroSchema.Parse(Complex);
        const string expected =
            """{"name":"shop.Order","type":"record","fields":[""" +
            """{"name":"id","type":"string"},""" +
            """{"name":"total","type":"bytes"},""" +
            """{"name":"status","type":{"name":"shop.Status","type":"enum","symbols":["NEW","PAID"]}},""" +
            """{"name":"lines","type":{"type":"array","items":{"name":"shop.Line","type":"record","fields":[{"name":"sku","type":{"name":"catalog.Sku","type":"fixed","size":8}},{"name":"qty","type":"int"}]}}},""" +
            """{"name":"next","type":["null","shop.Order"]},""" +
            """{"name":"skus","type":{"type":"map","values":"catalog.Sku"}},""" +
            """{"name":"root","type":{"name":"Root","type":"record","fields":[{"name":"s","type":"shop.Status"}]}}]}""";
        await Assert.That(schema.CanonicalForm).IsEqualTo(expected);
    }

    [Test]
    public async Task ToJson_Indented_ParsesToTheSameSchema()
    {
        var schema = AvroSchema.Parse(Complex);
        var indented = schema.ToJson(indented: true);
        await Assert.That(indented).Contains("\n");
        await Assert.That(AvroSchema.Parse(indented).ToJson()).IsEqualTo(schema.ToJson());
    }

    [Test]
    public async Task WriteTo_WritesTheSameJsonAsToJson()
    {
        var schema = AvroSchema.Parse(Complex);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            schema.WriteTo(writer);
        }

        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).IsEqualTo(schema.ToJson());
        await Assert.That(schema.ToString()).IsEqualTo(schema.ToJson());
    }

    [Test]
    public async Task Fingerprints_AreComputedOverTheCanonicalFormUtf8()
    {
        var schema = AvroSchema.Parse(Complex);
        var canonical = Encoding.UTF8.GetBytes(schema.CanonicalForm);

        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();
        await Assert.That(SchemaFingerprint.Md5(schema).SequenceEqual(md5.ComputeHash(canonical))).IsTrue();
        await Assert.That(SchemaFingerprint.Sha256(schema).SequenceEqual(sha256.ComputeHash(canonical))).IsTrue();
        await Assert.That(SchemaFingerprint.Crc64Avro(schema)).IsEqualTo(SchemaFingerprint.Crc64Avro(canonical));
    }

    [Test]
    public async Task Crc64Avro_OfEmptyInputIsTheEmptyConstant()
    {
        await Assert.That(unchecked((ulong)SchemaFingerprint.Crc64Avro([]))).IsEqualTo(SchemaFingerprint.Crc64AvroEmpty);
    }

    /// <summary>
    /// Control characters are escaped wherever they appear: as Jackson writes them in the JSON (the short forms, else
    /// upper-case \u00XX), and as the specification's canonical form requires (lower-case \u00xx, no short forms).
    /// </summary>
    [Test]
    public async Task ControlCharacters_AreEscaped_InTheJsonAndTheCanonicalForm()
    {
        // Names with control characters are only reachable with name validation disabled.
        const string Json = """{"type":"enum","name":"E\u001f","doc":"line\nbreak\u0001","symbols":["A\tB"],"k\u001e":"v\u0000"}""";
        var schema = AvroSchema.Parse(Json, new AvroSchemaParseOptions { ValidateNames = false });

        var written = schema.ToJson();
        var reparsed = (EnumSchema)AvroSchema.Parse(written, new AvroSchemaParseOptions { ValidateNames = false });

        await Assert.That(written).IsEqualTo("""{"type":"enum","name":"E\u001F","doc":"line\nbreak\u0001","symbols":["A\tB"],"k\u001E":"v\u0000"}""");
        await Assert.That(reparsed.Name.Name).IsEqualTo("E\u001f");
        await Assert.That(reparsed.Doc).IsEqualTo("line\nbreak\u0001");
        await Assert.That(reparsed.Symbols[0]).IsEqualTo("A\tB");
        await Assert.That(schema.CanonicalForm).IsEqualTo("""{"name":"E\u001f","type":"enum","symbols":["A\u0009B"]}""");
    }

    [Test]
    public async Task CanonicalForm_EscapesOnlyWhatJsonRequires()
    {
        // Only reachable with name validation disabled.
        var schema = AvroSchema.Parse("""{"type":"enum","name":"E","symbols":["a\"b","c\\d","é"]}""", new AvroSchemaParseOptions { ValidateNames = false });
        await Assert.That(schema.CanonicalForm).IsEqualTo("""{"name":"E","type":"enum","symbols":["a\"b","c\\d","é"]}""");
    }
}
