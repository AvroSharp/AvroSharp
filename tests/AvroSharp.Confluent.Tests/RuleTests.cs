using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Serialization;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Encryption;
using Confluent.SchemaRegistry.Rules;
using test.shop;
using static AvroSharp.Confluent.Tests.TestData;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent.Tests;

/// <summary>Data contract rules, run by Confluent's executors: CEL conditions and payload encryption.</summary>
public class RuleTests
{
    [Test]
    public async Task CelCondition_OnAGeneratedType()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Order>().Schema.ToJson(), Cel("message.customer == 'Ada'"));
        var serializer = new AvroSharpSerializer<Order>(registry, LatestVersion(), Rules(cel));

        var bytes = await serializer.SerializeAsync(NewOrder(), Value(topic));
        var other = NewOrder();
        other.Customer = "Bob";

        await Assert.That((await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(bytes, isNull: false, Value(topic))).Customer).IsEqualTo("Ada");
        await Assert.That(async () => await serializer.SerializeAsync(other, Value(topic))).Throws<SerializationException>();
    }

    [Test]
    public async Task CelCondition_OnAnAvroSerializableType()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Customer>().Schema.ToJson(), Cel("message.Name == 'Ada'"));
        var serializer = new AvroSharpSerializer<Customer>(registry, LatestVersion(), Rules(cel));

        var bytes = await serializer.SerializeAsync(new Customer { Name = "Ada", Level = 3 }, Value(topic));

        await Assert.That((await new AvroSharpDeserializer<Customer>(registry).DeserializeAsync(bytes, isNull: false, Value(topic))).Level).IsEqualTo(3);
        await Assert.That(async () => await serializer.SerializeAsync(new Customer { Name = "Bob" }, Value(topic))).Throws<SerializationException>();
    }

    // Confluent's CEL executor knows Apache.Avro records and plain .NET objects, not AvroValue: the rule fails, so the
    // message isn't written rather than written unchecked (#191).
    [Test]
    public async Task CelCondition_OnAGenericValue_FailsTheRule()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var schema = AvroTypes.Get<Customer>().Schema;
        await Register(registry, topic, schema.ToJson(), Cel("message.Name == 'Ada'"));
        var serializer = AvroSharpGeneric.CreateSerializer(registry, schema, LatestVersion(), Rules(cel));
        var ada = new GenericRecord((AvroSharp.Schemas.RecordSchema)schema) { ["Name"] = "Ada", ["Level"] = 3 };

        await Assert.That(async () => await serializer.SerializeAsync(ada, Value(topic))).Throws<SerializationException>();
    }

    [Test]
    public async Task PayloadEncryption_RoundTrips()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var encrypt = new Rule("encrypt", RuleKind.Transform, RuleMode.WriteRead, "ENCRYPT_PAYLOAD", null, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["encrypt.kek.name"] = "kek1",
            ["encrypt.kms.type"] = "local-kms",
            ["encrypt.kms.key.id"] = "mykey",
        });
        await Register(registry, topic, AvroTypes.Get<Customer>().Schema.ToJson(), new RuleSet([], [], [encrypt]));
        LocalKmsDriver.Register();
        using var keys = new InMemoryDekRegistry();
        using var encryption = new EncryptionExecutor(keys, new TestClock());
        var rules = Rules(encryption);
        var config = LatestVersion();
        config.Set("rules.secret", "mysecret");
        var customer = new Customer { Name = "Ada", Level = 3 };

        var bytes = await new AvroSharpSerializer<Customer>(registry, config, rules).SerializeAsync(customer, Value(topic));
        var back = await new AvroSharpDeserializer<Customer>(registry, null, rules).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(bytes.AsSpan(5).IndexOf("Ada"u8)).IsEqualTo(-1);
        await Assert.That(back.Name).IsEqualTo("Ada");
        await Assert.That(back.Level).IsEqualTo(3);
    }

    [Test]
    public async Task CelCondition_OnRead()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var schema = AvroTypes.Get<Customer>().Schema.ToJson();
        await Register(registry, topic, schema, Cel("message.Name == 'Ada'", RuleMode.Read));
        var serializer = new AvroSharpSerializer<Customer>(registry, LatestVersion());
        var deserializer = new AvroSharpDeserializer<Customer>(registry, null, Rules(cel));
        var ada = await serializer.SerializeAsync(new Customer { Name = "Ada" }, Value(topic));
        var bob = await serializer.SerializeAsync(new Customer { Name = "Bob" }, Value(topic));

        await Assert.That((await deserializer.DeserializeAsync(ada, isNull: false, Value(topic))).Name).IsEqualTo("Ada");
        await Assert.That(async () => await deserializer.DeserializeAsync(bob, isNull: false, Value(topic))).Throws<SerializationException>();
    }

    // Field rules change fields as the encoding is written or read, which AvroSharp.Confluent doesn't do yet (#186):
    // the rule fails, so a field isn't written unencrypted.
    [Test]
    public async Task FieldEncryption_IsNotSupported()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var encrypt = new Rule("encryptPII", RuleKind.Transform, RuleMode.WriteRead, "ENCRYPT", new HashSet<string>(StringComparer.Ordinal) { "PII" }, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["encrypt.kek.name"] = "kek1",
            ["encrypt.kms.type"] = "local-kms",
            ["encrypt.kms.key.id"] = "mykey",
        });
        await Register(registry, topic, """{"type":"record","name":"Customer","namespace":"test.native","fields":[{"name":"Name","type":"string","confluent:tags":["PII"]},{"name":"Level","type":"int"}]}""", new RuleSet([], [encrypt], []));
        LocalKmsDriver.Register();
        using var keys = new InMemoryDekRegistry();
        using var encryption = new FieldEncryptionExecutor(keys, new TestClock());
        var config = LatestVersion();
        config.Set("rules.secret", "mysecret");

        var write = async () => await new AvroSharpSerializer<Customer>(registry, config, Rules(encryption)).SerializeAsync(new Customer { Name = "Ada" }, Value(topic));

        var error = await Assert.That(write).ThrowsException();
        var notSupported = false;
        for (var e = error; e is not null; e = e.InnerException)
        {
            notSupported |= e is NotSupportedException && e.Message.Contains("encryptPII", StringComparison.Ordinal);
        }

        await Assert.That(notSupported).IsTrue();
    }

    [Test]
    public async Task MigrationRules_AreNotSupported()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<Customer>(registry).SerializeAsync(new Customer { Name = "Ada" }, Value(topic));
        var upgrade = new Rule("upgrade", RuleKind.Transform, RuleMode.Upgrade, "JSONATA", null, null, "$merge([$, {'Tier': 1}])", null, null, false);
        await Register(registry, topic, """{"type":"record","name":"Customer","namespace":"test.native","fields":[{"name":"Name","type":"string"},{"name":"Level","type":"int"},{"name":"Tier","type":"int","default":0}]}""", new RuleSet([upgrade], [], []));
        var deserializer = new AvroSharpDeserializer<Customer>(registry, new AvroSharpDeserializerConfig { UseLatestVersion = true });

        await Assert.That(async () => await deserializer.DeserializeAsync(bytes, isNull: false, Value(topic)))
            .Throws<NotSupportedException>().WithMessageContaining("migration rules", StringComparison.Ordinal);
    }

    private static RuleSet Cel(string expression, RuleMode mode = RuleMode.Write) =>
        new([], [new Rule("check", RuleKind.Condition, mode, "CEL", null, null, expression, null, null, false)], []);

    private static AvroSharpSerializerConfig LatestVersion() => new() { UseLatestVersion = true, AutoRegisterSchemas = false };

    private static RuleRegistry Rules(IRuleExecutor executor)
    {
        var rules = new RuleRegistry();
        rules.RegisterExecutor(executor);
        return rules;
    }

    private static Task<int> Register(InMemorySchemaRegistry registry, string topic, string schema, RuleSet rules) =>
        registry.RegisterSchemaAsync(topic + "-value", new ConfluentSchema(schema, [], SchemaType.Avro, null, rules));
}

/// <summary>A type with an AvroSharp schema of its own: not an Apache.Avro ISpecificRecord.</summary>
[AvroSerializable(Namespace = "test.native")]
public partial class Customer
{
    public string Name { get; set; } = "";

    public int Level { get; set; }
}

internal sealed class TestClock : IClock
{
    public long NowToUnixTimeMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
