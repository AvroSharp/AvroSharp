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

    // CEL names the Avro fields, as rules written for Java's and Confluent's serializers do, not the C# properties (#191).
    [Test]
    public async Task CelCondition_OnAnAvroSerializableType_NamesTheAvroFields()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), Cel("message.name == 'Ada' && message.tier_level > 2 && message.home.city == 'London'"));
        var serializer = new AvroSharpSerializer<Member>(registry, LatestVersion(), Rules(cel));

        var bytes = await serializer.SerializeAsync(NewMember("Ada", 3), Value(topic));

        await Assert.That((await new AvroSharpDeserializer<Member>(registry).DeserializeAsync(bytes, isNull: false, Value(topic))).Level).IsEqualTo(3);
        await Assert.That(async () => await serializer.SerializeAsync(NewMember("Bob", 3), Value(topic))).Throws<SerializationException>();
        await Assert.That(async () => await serializer.SerializeAsync(NewMember("Ada", 1), Value(topic))).Throws<SerializationException>();
    }

    // A passing condition writes the value's own encoding: the body is the one written without rules.
    [Test]
    public async Task CelCondition_WritesTheSameBytes()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), Cel("message.name == 'Ada'"));

        var withRule = await new AvroSharpSerializer<Member>(registry, LatestVersion(), Rules(cel)).SerializeAsync(NewMember("Ada", 3), Value(topic));
        var without = await new AvroSharpSerializer<Member>(registry).SerializeAsync(NewMember("Ada", 3), Value(Topic()));

        await Assert.That(withRule.AsSpan(5).SequenceEqual(without.AsSpan(5))).IsTrue();
    }

    [Test]
    public async Task CelCondition_OnAGenericValue()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var schema = AvroTypes.Get<Customer>().Schema;
        await Register(registry, topic, schema.ToJson(), Cel("message.Name == 'Ada'"));
        var serializer = AvroSharpGeneric.CreateSerializer(registry, schema, LatestVersion(), Rules(cel));
        var ada = new GenericRecord((AvroSharp.Schemas.RecordSchema)schema) { ["Name"] = "Ada", ["Level"] = 3 };
        var bob = new GenericRecord((AvroSharp.Schemas.RecordSchema)schema) { ["Name"] = "Bob", ["Level"] = 3 };

        var bytes = await serializer.SerializeAsync(ada, Value(topic));

        await Assert.That((await new AvroSharpDeserializer<Customer>(registry).DeserializeAsync(bytes, isNull: false, Value(topic))).Name).IsEqualTo("Ada");
        await Assert.That(async () => await serializer.SerializeAsync(bob, Value(topic))).Throws<SerializationException>();
    }

    // A transform that changes the record (here in place, as a custom executor may) changes what is written.
    [Test]
    public async Task Transform_ChangesTheRecord_OnWrite()
    {
        using var cel = new CelExecutor();
        using var upper = new UpperCaseName();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), new RuleSet([], [
            new Rule("check", RuleKind.Condition, RuleMode.Write, "CEL", null, null, "message.name != ''", null, null, false),
            new Rule("upper", RuleKind.Transform, RuleMode.Write, UpperCaseName.RuleType, null, null, null, null, null, false),
        ], []));
        var rules = Rules(cel);
        rules.RegisterExecutor(upper);

        var bytes = await new AvroSharpSerializer<Member>(registry, LatestVersion(), rules).SerializeAsync(NewMember("ada", 3), Value(topic));
        var written = await new AvroSharpDeserializer<Member>(registry, null, new RuleRegistry()).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(written.Name).IsEqualTo("ADA");
        await Assert.That(written.Level).IsEqualTo(3);
        await Assert.That(written.Home.City).IsEqualTo("London");
    }

    // And what is read.
    [Test]
    public async Task Transform_ChangesTheRecord_OnRead()
    {
        using var cel = new CelExecutor();
        using var upper = new UpperCaseName();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), new RuleSet([], [
            new Rule("check", RuleKind.Condition, RuleMode.Read, "CEL", null, null, "message.name != ''", null, null, false),
            new Rule("upper", RuleKind.Transform, RuleMode.Read, UpperCaseName.RuleType, null, null, null, null, null, false),
        ], []));
        var rules = Rules(cel);
        rules.RegisterExecutor(upper);

        var bytes = await new AvroSharpSerializer<Member>(registry, LatestVersion(), new RuleRegistry()).SerializeAsync(NewMember("bob", 3), Value(topic));
        var read = await new AvroSharpDeserializer<Member>(registry, null, rules).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(read.Name).IsEqualTo("BOB");
        await Assert.That(read.Level).IsEqualTo(3);
        await Assert.That(read.Home.City).IsEqualTo("London");
    }

    // A CEL transform can return a value that isn't a record of the schema, such as a map: it isn't written.
    [Test]
    public async Task Transform_ReturningAnotherShape_IsAnError()
    {
        using var cel = new CelExecutor();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var rules = new RuleSet([], [new Rule("t", RuleKind.Transform, RuleMode.Write, "CEL", null, null, "{'name': 'Bob'}", null, null, false)], []);
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), rules);
        var serializer = new AvroSharpSerializer<Member>(registry, LatestVersion(), Rules(cel));

        await Assert.That(async () => await serializer.SerializeAsync(NewMember("Ada", 3), Value(topic)))
            .Throws<InvalidOperationException>().WithMessageContaining("isn't one of the schema", StringComparison.Ordinal);
    }

    // Without CEL rules, domain rules see the value itself.
    [Test]
    public async Task OtherDomainRules_SeeTheValue()
    {
        using var recorder = new RecordMessageType();
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var rules = new RuleSet([], [new Rule("r", RuleKind.Condition, RuleMode.Write, RecordMessageType.RuleType, null, null, null, null, null, false)], []);
        await Register(registry, topic, AvroTypes.Get<Member>().Schema.ToJson(), rules);

        await new AvroSharpSerializer<Member>(registry, LatestVersion(), Rules(recorder)).SerializeAsync(NewMember("Ada", 3), Value(topic));

        await Assert.That(recorder.Seen).IsEqualTo(typeof(Member));
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
        var schema = AvroTypes.Get<Member>().Schema.ToJson();
        await Register(registry, topic, schema, Cel("message.name == 'Ada'", RuleMode.Read));
        var serializer = new AvroSharpSerializer<Member>(registry, LatestVersion());
        var deserializer = new AvroSharpDeserializer<Member>(registry, null, Rules(cel));
        var ada = await serializer.SerializeAsync(NewMember("Ada", 0), Value(topic));
        var bob = await serializer.SerializeAsync(NewMember("Bob", 0), Value(topic));

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

    private static Member NewMember(string name, int level) => new() { Name = name, Level = level, Home = new Address { City = "London" } };

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

/// <summary>A type whose Avro field names differ from its C# property names.</summary>
[AvroSerializable(Namespace = "test.native")]
public partial class Member
{
    [AvroName("name")]
    public string Name { get; set; } = "";

    [AvroName("tier_level")]
    public int Level { get; set; }

    [AvroName("home")]
    public Address Home { get; set; } = new();
}

[AvroSerializable(Namespace = "test.native")]
public partial class Address
{
    [AvroName("city")]
    public string City { get; set; } = "";
}

/// <summary>A transform that upper-cases a record's <c>name</c> in place, in Apache.Avro's generic model.</summary>
internal sealed class UpperCaseName : IRuleExecutor
{
    public const string RuleType = "UPPER_NAME";

    public void Configure(IEnumerable<KeyValuePair<string, string>> config, ISchemaRegistryClient? client = null)
    {
    }

    public string Type() => RuleType;

    public Task<object> Transform(RuleContext ctx, object message)
    {
        var record = (Avro.Generic.GenericRecord)message;
        record.Add("name", ((string)record["name"]).ToUpperInvariant());
        return Task.FromResult(message);
    }

    public void Dispose()
    {
    }
}

/// <summary>A condition that records the type of the message it is given.</summary>
internal sealed class RecordMessageType : IRuleExecutor
{
    public const string RuleType = "RECORD_TYPE";

    public System.Type? Seen { get; private set; }

    public void Configure(IEnumerable<KeyValuePair<string, string>> config, ISchemaRegistryClient? client = null)
    {
    }

    public string Type() => RuleType;

    public Task<object> Transform(RuleContext ctx, object message)
    {
        Seen = message.GetType();
        return Task.FromResult<object>(true);
    }

    public void Dispose()
    {
    }
}

internal sealed class TestClock : IClock
{
    public long NowToUnixTimeMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
