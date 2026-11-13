using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Encryption;
using Confluent.SchemaRegistry.Rules;
using Confluent.SchemaRegistry.Serdes;
using static AvroSharp.Confluent.Tests.TestData;
using ApacheGenericRecord = Avro.Generic.GenericRecord;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// Field rules (#186): field-level encryption (<c>ENCRYPT</c>, with a local KMS) and <c>CEL_FIELD</c>, on the fields the
/// rules' tags name, against Confluent's own serde on the same registry and keys.
/// </summary>
public class FieldRuleTests
{
    // Customer's schema (test.native.Customer), with the Name field tagged PII.
    private const string Tagged = """{"type":"record","name":"Customer","namespace":"test.native","fields":[{"name":"Name","type":"string","confluent:tags":["PII"]},{"name":"Level","type":"int"}]}""";

    [Test]
    public async Task FieldEncryption_EncryptsTheTaggedField_AndEachSerdeReadsTheOthers()
    {
        using var keys = new InMemoryDekRegistry();
        using var encryption = new FieldEncryptionExecutor(keys, new TestClock());
        var rules = Rules(encryption);
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, Tagged, new RuleSet([], [EncryptPii()], []));

        var ours = await new AvroSharpSerializer<Customer>(registry, Latest(), rules).SerializeAsync(new Customer { Name = "Ada", Level = 3 }, Value(topic));
        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(Tagged);
        var record = new ApacheGenericRecord(apacheSchema);
        record.Add("Name", "Grace");
        record.Add("Level", 4);
        var theirs = await new AvroSerializer<ApacheGenericRecord>(registry, ConfluentLatest(), rules).SerializeAsync(record, Value(topic));

        // The name is encrypted, the level isn't.
        await Assert.That(Contains(ours!, "Ada")).IsFalse();
        await Assert.That(Contains(theirs, "Grace")).IsFalse();

        var oursReadByThem = await new AvroDeserializer<ApacheGenericRecord>(registry, null, rules).DeserializeAsync(ours, isNull: false, Value(topic));
        var theirsReadByUs = await new AvroSharpDeserializer<Customer>(registry, null, rules).DeserializeAsync(theirs, isNull: false, Value(topic));
        var oursReadByUs = new AvroSharpDeserializer<Customer>(registry, null, rules).Deserialize(ours, isNull: false, Value(topic));
        await Assert.That(oursReadByThem["Name"]).IsEqualTo("Ada");
        await Assert.That(oursReadByThem["Level"]).IsEqualTo(3);
        await Assert.That(theirsReadByUs.Name).IsEqualTo("Grace");
        await Assert.That(theirsReadByUs.Level).IsEqualTo(4);
        await Assert.That(oursReadByUs.Name).IsEqualTo("Ada");
    }

    [Test]
    public async Task FieldEncryption_OfGenericValues_AndOfFieldsInsideArraysAndUnions()
    {
        const string Nested = """
            {"type":"record","name":"Account","namespace":"test.native","fields":[
              {"name":"owner","type":["null","string"],"confluent:tags":["PII"]},
              {"name":"contacts","type":{"type":"array","items":{"type":"record","name":"Contact","fields":[
                {"name":"email","type":"string","confluent:tags":["PII"]},{"name":"kind","type":"string"}]}}}]}
            """;
        using var keys = new InMemoryDekRegistry();
        using var encryption = new FieldEncryptionExecutor(keys, new TestClock());
        var rules = Rules(encryption);
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, Nested, new RuleSet([], [EncryptPii()], []));
        var schema = (RecordSchema)AvroSchema.Parse(Nested);
        var contact = (RecordSchema)((ArraySchema)schema.Fields[1].Schema).Items;
        var value = AvroValue.FromRecord(new GenericRecord(schema)
        {
            ["owner"] = "Ada",
            ["contacts"] = AvroValue.FromArray([AvroValue.FromRecord(new GenericRecord(contact) { ["email"] = "ada@example.com", ["kind"] = "work" })]),
        });

        var bytes = await AvroSharpGeneric.CreateSerializer(registry, schema, Latest(), rules).SerializeAsync(value, Value(topic));
        var ours = (await AvroSharpGeneric.CreateDeserializer(registry, ruleRegistry: rules).DeserializeAsync(bytes, isNull: false, Value(topic))).AsRecord();
        var theirs = await new AvroDeserializer<ApacheGenericRecord>(registry, null, rules).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(Contains(bytes!, "ada@example.com")).IsFalse();
        await Assert.That(Contains(bytes!, "Ada")).IsFalse();
        await Assert.That(Contains(bytes!, "work")).IsTrue();
        await Assert.That(ours["owner"].AsString()).IsEqualTo("Ada");
        await Assert.That(ours["contacts"].AsArray()[0].AsRecord()["email"].AsString()).IsEqualTo("ada@example.com");
        await Assert.That(theirs["owner"]).IsEqualTo("Ada");
    }

    [Test]
    public async Task CelField_TransformsAndChecksTheFieldsItNames()
    {
        using var cel = new CelFieldExecutor();
        var rules = Rules(cel);
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var upper = new Rule("shout", RuleKind.Transform, RuleMode.Write, "CEL_FIELD", null, null, "name == 'Name' ; value + '!'", null, null, false);
        var positive = new Rule("positive", RuleKind.Condition, RuleMode.Write, "CEL_FIELD", null, null, "name == 'Level' ; value > 0", null, null, false);
        await Register(registry, topic, Tagged, new RuleSet([], [upper, positive], []));
        var serializer = new AvroSharpSerializer<Customer>(registry, Latest(), rules);

        var bytes = await serializer.SerializeAsync(new Customer { Name = "Ada", Level = 3 }, Value(topic));
        var read = await new AvroSharpDeserializer<Customer>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        var theirs = await new AvroSerializer<ApacheGenericRecord>(registry, ConfluentLatest(), rules).SerializeAsync(ApacheCustomer("Ada", 3), Value(topic));

        await Assert.That(read.Name).IsEqualTo("Ada!");
        await Assert.That(bytes).IsEquivalentTo(theirs, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(async () => await serializer.SerializeAsync(new Customer { Name = "Ada", Level = -1 }, Value(topic))).ThrowsException();
    }

    /// <summary>A null tagged field is not given to the rule, as in Confluent's serde, where size(value) of null would fail.</summary>
    [Test]
    public async Task CelField_SkipsNullFields_AsConfluentDoes()
    {
        const string Owned = """{"type":"record","name":"Account","namespace":"test.native","fields":[{"name":"owner","type":["null","string"],"confluent:tags":["PII"]}]}""";
        using var cel = new CelFieldExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var notEmpty = new Rule("not-empty", RuleKind.Condition, RuleMode.Write, "CEL_FIELD", new HashSet<string>(StringComparer.Ordinal) { "PII" }, null, "size(value) > 0", null, null, false);
        await Register(registry, topic, Owned, new RuleSet([], [notEmpty], []));
        var schema = (RecordSchema)AvroSchema.Parse(Owned);

        var serializer = AvroSharpGeneric.CreateSerializer(registry, schema, Latest(), Rules(cel));
        var withNull = await serializer.SerializeAsync(AvroValue.FromRecord(new GenericRecord(schema) { ["owner"] = AvroValue.Null }), Value(topic));
        var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(Owned);
        var record = new ApacheGenericRecord(apacheSchema);
        record.Add("owner", null);
        var theirs = await new AvroSerializer<ApacheGenericRecord>(registry, ConfluentLatest(), Rules(cel)).SerializeAsync(record, Value(topic));

        await Assert.That(withNull).IsEquivalentTo(theirs, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(async () => await serializer.SerializeAsync(AvroValue.FromRecord(new GenericRecord(schema) { ["owner"] = string.Empty }), Value(topic))).ThrowsException();
    }

    [Test]
    public async Task ARuleWithoutTags_AppliesToEveryField()
    {
        using var cel = new CelFieldExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        // typeName names the field's type, so only the string fields change.
        var rule = new Rule("strings", RuleKind.Transform, RuleMode.Write, "CEL_FIELD", null, null, "typeName == 'STRING' ; value + '?'", null, null, false);
        await Register(registry, topic, Tagged, new RuleSet([], [rule], []));

        var bytes = await new AvroSharpSerializer<Customer>(registry, Latest(), Rules(cel)).SerializeAsync(new Customer { Name = "Ada", Level = 3 }, Value(topic));
        var read = await new AvroSharpDeserializer<Customer>(registry).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(read.Name).IsEqualTo("Ada?");
        await Assert.That(read.Level).IsEqualTo(3);
    }

    private static Rule EncryptPii() =>
        new("encryptPII", RuleKind.Transform, RuleMode.WriteRead, "ENCRYPT", new HashSet<string>(StringComparer.Ordinal) { "PII" }, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["encrypt.kek.name"] = "kek1",
            ["encrypt.kms.type"] = "local-kms",
            ["encrypt.kms.key.id"] = "mykey",
        });

    private static ApacheGenericRecord ApacheCustomer(string name, int level)
    {
        var record = new ApacheGenericRecord((Avro.RecordSchema)Avro.Schema.Parse(Tagged));
        record.Add("Name", name);
        record.Add("Level", level);
        return record;
    }

    private static AvroSharpSerializerConfig Latest()
    {
        LocalKmsDriver.Register();
        var config = new AvroSharpSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false };
        config.Set("rules.secret", "mysecret");
        return config;
    }

    private static AvroSerializerConfig ConfluentLatest()
    {
        var config = new AvroSerializerConfig { UseLatestVersion = true, AutoRegisterSchemas = false };
        config.Set("rules.secret", "mysecret");
        return config;
    }

    private static bool Contains(byte[] message, string text) => message.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static RuleRegistry Rules(IRuleExecutor executor)
    {
        var rules = new RuleRegistry();
        rules.RegisterExecutor(executor);
        return rules;
    }

    private static Task<int> Register(InMemorySchemaRegistry registry, string topic, string schema, RuleSet rules) =>
        registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(schema, [], SchemaType.Avro, null, rules));
}
