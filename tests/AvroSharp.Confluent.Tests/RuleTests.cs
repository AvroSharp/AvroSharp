using System;
using System.Collections.Generic;
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

        await Assert.That(bytes).IsNotNull();
        await Assert.That(async () => await serializer.SerializeAsync(other, Value(topic))).ThrowsException();
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

        await Assert.That(bytes).IsNotNull();
        await Assert.That(async () => await serializer.SerializeAsync(new Customer { Name = "Bob" }, Value(topic))).ThrowsException();
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

        await Assert.That(async () => await serializer.SerializeAsync(ada, Value(topic))).Throws<System.Runtime.Serialization.SerializationException>();
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

    private static RuleSet Cel(string expression) =>
        new([], [new Rule("check", RuleKind.Condition, RuleMode.Write, "CEL", null, null, expression, null, null, false)], []);

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
