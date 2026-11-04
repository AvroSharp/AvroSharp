using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Rules;
using Confluent.SchemaRegistry.Serdes;
using static AvroSharp.Confluent.Tests.TestData;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// Migration rules (UPGRADE and DOWNGRADE, in JSONata) between a subject's versions (#187): run on the value as Avro
/// JSON, as Confluent's deserializer runs them, and compared with it.
/// </summary>
public class MigrationTests
{
    private const string V1 = """{"type":"record","name":"Person","namespace":"test.migration","fields":[{"name":"name","type":"string"},{"name":"age","type":"int"}]}""";
    private const string V2 = """{"type":"record","name":"Person","namespace":"test.migration","fields":[{"name":"full_name","type":"string"},{"name":"age","type":"int"}]}""";

    // v2 renames name to full_name; its rules map between the two.
    private static readonly RuleSet Renamed = new(
        [
            new Rule("rename", RuleKind.Transform, RuleMode.Upgrade, "JSONATA", null, null, "$merge([$sift($, function($v, $k) {$k != 'name'}), {'full_name': $.'name'}])", null, null, false),
            new Rule("unrename", RuleKind.Transform, RuleMode.Downgrade, "JSONATA", null, null, "$merge([$sift($, function($v, $k) {$k != 'full_name'}), {'name': $.'full_name'}])", null, null, false),
        ],
        [],
        []);

    [Test]
    public async Task AnUpgradeRule_RenamesTheField_AsConfluentsDeserializer()
    {
        using var jsonata = new JsonataExecutor();
        var rules = Rules(jsonata);
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await WriteV1(registry, topic);
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(V2, [], SchemaType.Avro, null, Renamed));
        var latest = new AvroSharpDeserializerConfig { UseLatestVersion = true };

        var generic = (await AvroSharpGeneric.CreateDeserializer(registry, config: latest, ruleRegistry: rules).DeserializeAsync(bytes, isNull: false, Value(topic))).AsRecord();
        var typed = await new AvroSharpDeserializer<Person>(registry, latest, rules).DeserializeAsync(bytes, isNull: false, Value(topic));
        var sync = new AvroSharpDeserializer<Person>(registry, latest, rules).Deserialize(bytes, isNull: false, Value(topic));
        var theirs = await new AvroDeserializer<ApacheGenericRecord>(registry, new AvroDeserializerConfig { UseLatestVersion = true }, rules).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(generic["full_name"].AsString()).IsEqualTo("Ada");
        await Assert.That(generic["age"].AsInt32()).IsEqualTo(36);
        await Assert.That(typed.FullName).IsEqualTo("Ada");
        await Assert.That(typed.Age).IsEqualTo(36);
        await Assert.That(sync.FullName).IsEqualTo("Ada");
        await Assert.That(theirs["full_name"]).IsEqualTo("Ada");
        await Assert.That(theirs["age"]).IsEqualTo(36);
    }

    [Test]
    public async Task ADowngradeRule_RunsForAnOlderTargetVersion_AsConfluentsDeserializer()
    {
        using var jsonata = new JsonataExecutor();
        var rules = Rules(jsonata);
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(V1, [], SchemaType.Avro, new Metadata(null!, new Dictionary<string, string> { ["major"] = "1" }, null!), null));
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(V2, [], SchemaType.Avro, new Metadata(null!, new Dictionary<string, string> { ["major"] = "2" }, null!), Renamed));
        var v2 = (RecordSchema)AvroSchema.Parse(V2);
        var bytes = await AvroSharpGeneric.CreateSerializer(registry, v2, new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false }, rules)
            .SerializeAsync(AvroValue.FromRecord(new GenericRecord(v2) { ["full_name"] = "Ada", ["age"] = 36 }), Value(topic));
        var major1 = new Dictionary<string, string> { ["major"] = "1" };

        var ours = (await AvroSharpGeneric.CreateDeserializer(registry, config: new AvroSharpDeserializerConfig { UseLatestWithMetadata = major1 }, ruleRegistry: rules).DeserializeAsync(bytes, isNull: false, Value(topic))).AsRecord();
        var theirs = await new AvroDeserializer<ApacheGenericRecord>(registry, new AvroDeserializerConfig { UseLatestWithMetadata = major1 }, rules).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(ours["name"].AsString()).IsEqualTo("Ada");
        await Assert.That(ours.Schema.Fields.Count).IsEqualTo(2);
        await Assert.That(theirs["name"]).IsEqualTo("Ada");
    }

    [Test]
    public async Task WithoutMigrationRules_NothingIsMigrated()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await WriteV1(registry, topic);
        await registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(
            """{"type":"record","name":"Person","namespace":"test.migration","fields":[{"name":"name","type":"string"},{"name":"age","type":"int"},{"name":"tier","type":"int","default":0}]}""", SchemaType.Avro));

        var read = (await AvroSharpGeneric.CreateDeserializer(registry, config: new AvroSharpDeserializerConfig { UseLatestVersion = true }).DeserializeAsync(bytes, isNull: false, Value(topic))).AsRecord();

        await Assert.That(read["name"].AsString()).IsEqualTo("Ada");
        await Assert.That(read["tier"].AsInt32()).IsEqualTo(0);
    }

    // JSONata expressions are written against Apache's JsonEncoder shape (wrapped unions by full name, bytes as
    // ISO-8859-1 strings, enums as symbols, logical types as their underlying values), which the rules see.
    [Test]
    public async Task TheRulesSeeApachesJsonShape()
    {
        const string Json = """
            {"type":"record","name":"Shape","namespace":"test.migration","fields":[
              {"name":"u","type":["null","string",{"type":"record","name":"Inner","fields":[{"name":"x","type":"int"}]}]},
              {"name":"v","type":["null","string"]},
              {"name":"e","type":{"type":"enum","name":"Color","symbols":["RED","GREEN"]}},
              {"name":"b","type":"bytes"},
              {"name":"f","type":{"type":"fixed","name":"Two","size":2}},
              {"name":"d","type":{"type":"bytes","logicalType":"decimal","precision":6,"scale":2}},
              {"name":"t","type":{"type":"long","logicalType":"timestamp-millis"}},
              {"name":"m","type":{"type":"map","values":"double"}},
              {"name":"a","type":{"type":"array","items":"boolean"}}]}
            """;
        var schema = (RecordSchema)AvroSchema.Parse(Json);
        var inner = new GenericRecord((RecordSchema)((UnionSchema)schema.Fields[0].Schema).Branches[2]) { ["x"] = 7 };
        var value = AvroValue.FromRecord(new GenericRecord(schema)
        {
            ["u"] = AvroValue.FromRecord(inner),
            ["v"] = "hi",
            ["e"] = AvroValue.FromEnum((EnumSchema)schema.Fields[2].Schema, "GREEN"),
            ["b"] = AvroValue.FromBytes([0, 200, 65]),
            ["f"] = AvroValue.FromFixed(new GenericFixed((FixedSchema)schema.Fields[4].Schema, [1, 255])),
            ["d"] = AvroValue.FromBytes([0x30, 0x39]),
            ["t"] = 1_700_000_000_000L,
            ["m"] = AvroValue.FromMap(new Dictionary<string, AvroValue> { ["k"] = 1.5 }),
            ["a"] = AvroValue.FromArray([true, false]),
        });

        var ours = GenericDatumJsonWriter.Create(schema).WriteToString(value);
        var apacheSchema = Avro.Schema.Parse(Json);
        var record = new Avro.Generic.GenericDatumReader<object>(apacheSchema, apacheSchema).Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(GenericDatumWriter.Create(schema).WriteToArray(value))));
        using var output = new MemoryStream();
        var encoder = new Avro.IO.JsonEncoder(apacheSchema, output);
        new Avro.Generic.GenericDatumWriter<object>(apacheSchema).Write(record, encoder);
        encoder.Flush();
        var theirs = Encoding.UTF8.GetString(output.ToArray());

        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(ours), JsonNode.Parse(theirs))).IsTrue().Because($"ours {ours}, Apache's {theirs}");
    }

    private static RuleRegistry Rules(IRuleExecutor executor)
    {
        var rules = new RuleRegistry();
        rules.RegisterExecutor(executor);
        return rules;
    }

    private static Task<byte[]?> WriteV1(InMemorySchemaRegistry registry, string topic)
    {
        var v1 = (RecordSchema)AvroSchema.Parse(V1);
        return AvroSharpGeneric.CreateSerializer(registry, v1).SerializeAsync(AvroValue.FromRecord(new GenericRecord(v1) { ["name"] = "Ada", ["age"] = 36 }), Value(topic));
    }
}

/// <summary>The type of version 2 of the subject, with its renamed field.</summary>
[AvroSerializable(Namespace = "test.migration")]
public partial class Person
{
    [AvroName("full_name")]
    public string FullName { get; set; } = "";

    [AvroName("age")]
    public int Age { get; set; }
}
