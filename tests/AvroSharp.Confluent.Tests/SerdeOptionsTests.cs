using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Confluent.Kafka;
using Confluent.SchemaRegistry.Serdes;
using test.shop;
using TUnit.Assertions.Enums;
using static AvroSharp.Confluent.Tests.ConfluentInteropTests;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// The smaller serde options: <c>fail.on.trailing.data</c>, Confluent.Kafka's <see cref="Null"/>,
/// <see cref="TombstoneBehavior"/> and <c>remove.java.properties</c>.
/// </summary>
public class SerdeOptionsTests
{
    private static readonly AvroSharpDeserializerConfig FailOnTrailing = new() { FailOnTrailingData = true };
    private static readonly AvroSharpSerializerConfig StrictWriter = new() { TombstoneBehavior = TombstoneBehavior.Strict };
    private static readonly AvroSharpDeserializerConfig StrictReader = new() { TombstoneBehavior = TombstoneBehavior.Strict };

    [Test]
    public async Task TrailingData_FailsWhenOn_AndReadsWhenOff_Record()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<Order>(registry).SerializeAsync(NewOrder(), Value(topic));
        var extended = bytes!.Append((byte)0).ToArray();

        await AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(extended, isNull: false, Value(topic)));
        await AssertIsNewOrder(await new AvroSharpDeserializer<Order>(registry, FailOnTrailing).DeserializeAsync(bytes, isNull: false, Value(topic)));
        await Assert.That(async () => await new AvroSharpDeserializer<Order>(registry, FailOnTrailing).DeserializeAsync(extended, isNull: false, Value(topic)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task TrailingData_FailsWhenOn_AndReadsWhenOff_Primitive_SyncAndGeneric()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<int>(registry).SerializeAsync(42, Value(topic));
        var extended = bytes!.Append((byte)7).ToArray();

        await Assert.That(new AvroSharpDeserializer<int>(registry).Deserialize(extended, isNull: false, Value(topic))).IsEqualTo(42);
        var strict = new AvroSharpDeserializer<int>(registry, FailOnTrailing);
        await Assert.That(strict.Deserialize(bytes, isNull: false, Value(topic))).IsEqualTo(42);
        // Once the schema ID's reader is known, the synchronous path checks too.
        await Assert.That(() => strict.Deserialize(extended, isNull: false, Value(topic))).Throws<InvalidDataException>();
        var generic = AvroSharpGeneric.CreateDeserializer(registry, config: FailOnTrailing);
        await Assert.That(async () => await generic.DeserializeAsync(extended, isNull: false, Value(topic))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task TrailingData_DoesNotApplyToRawBytes()
    {
        // A "bytes" value is the whole message body, so nothing trails it.
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var bytes = await new AvroSharpSerializer<byte[]>(registry).SerializeAsync([1, 2, 3], Value(topic));

        var read = await new AvroSharpDeserializer<byte[]>(registry, FailOnTrailing).DeserializeAsync(bytes, isNull: false, Value(topic));

        await Assert.That(read).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Null_WritesConfluentsBytes_AndEachReadsTheOther()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        var theirs = await new AvroSerializer<Null>(registry).SerializeAsync(null!, Value(topic));
        var ours = await new AvroSharpSerializer<Null>(registry).SerializeAsync(null!, Value(topic));

        await Assert.That(ours).IsNotNull();
        await Assert.That(ours).IsEquivalentTo(theirs!, CollectionOrdering.Matching);
        // One version: AvroSharp registered the schema Confluent did ({"type":"null"}).
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1);
        await Assert.That(new AvroSharpSerializer<Null>(registry).Serialize(null!, Value(topic))).IsEquivalentTo(theirs!, CollectionOrdering.Matching);

        await Assert.That(await new AvroSharpDeserializer<Null>(registry).DeserializeAsync(theirs, isNull: false, Value(topic))).IsNull();
        await Assert.That(new AvroSharpDeserializer<Null>(registry).Deserialize(theirs, isNull: false, Value(topic))).IsNull();
        await Assert.That(await new AvroDeserializer<Null>(registry).DeserializeAsync(ours, isNull: false, Value(topic))).IsNull();
        await Assert.That(await new AvroSharpDeserializer<Null>(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic))).IsNull();
    }

    [Test]
    public async Task Strict_RejectsValueTypes_AndSchemasThatHoldNull()
    {
        using var registry = new InMemorySchemaRegistry();

        await Assert.That(() => new AvroSharpSerializer<int>(registry, StrictWriter)).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpDeserializer<long>(registry, StrictReader)).Throws<ArgumentException>();
        await Assert.That(() => new AvroSharpSerializer<Null>(registry, StrictWriter)).Throws<InvalidOperationException>();
        var nullable = AvroSchema.Parse("""["null", "string"]""");
        await Assert.That(() => AvroSharpGeneric.CreateSerializer(registry, nullable, StrictWriter)).Throws<InvalidOperationException>();

        // Reference types, Nullable<T> and AvroValue of a schema without null are fine.
        _ = new AvroSharpSerializer<Order>(registry, StrictWriter);
        _ = new AvroSharpSerializer<string>(registry, StrictWriter);
        _ = new AvroSharpDeserializer<Order>(registry, StrictReader);
        _ = AvroSharpGeneric.CreateSerializer(registry, AvroSchema.Parse("\"string\""), StrictWriter);
        _ = AvroSharpGeneric.CreateDeserializer(registry, config: StrictReader);
        _ = AvroSharpGeneric.CreateSerializer(registry, StrictWriter);
    }

    [Test]
    public async Task Strict_DeserializerRejectsAWriterSchemaThatHoldsNull()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var nullable = AvroSchema.Parse("""["null", "string"]""");
        var bytes = await AvroSharpGeneric.CreateSerializer(registry, nullable).SerializeAsync(AvroValue.FromString("x"), Value(topic));

        await Assert.That(await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(bytes, isNull: false, Value(topic))).IsEqualTo(AvroValue.FromString("x"));
        await Assert.That(async () => await AvroSharpGeneric.CreateDeserializer(registry, config: StrictReader).DeserializeAsync(bytes, isNull: false, Value(topic)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Strict_ValueTombstones_WriteAndReadAsNone()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        await Assert.That(await new AvroSharpSerializer<Order>(registry, StrictWriter).SerializeAsync(null!, Value(topic))).IsNull();
        await Assert.That(new AvroSharpSerializer<Order>(registry, StrictWriter).Serialize(null!, Value(topic))).IsNull();
        await Assert.That(await AvroSharpGeneric.CreateSerializer(registry, AvroSchema.Parse("\"string\""), StrictWriter).SerializeAsync(AvroValue.Null, Value(topic))).IsNull();
        await Assert.That(await AvroSharpGeneric.CreateSerializer(registry, StrictWriter).SerializeAsync(AvroValue.Null, Value(topic))).IsNull();
        await Assert.That(await new AvroSharpDeserializer<Order>(registry, StrictReader).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic))).IsNull();
        await Assert.That(new AvroSharpDeserializer<Order>(registry, StrictReader).Deserialize(ReadOnlySpan<byte>.Empty, isNull: true, Value(topic))).IsNull();
        await Assert.That((await AvroSharpGeneric.CreateDeserializer(registry, config: StrictReader).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Value(topic))).IsNull).IsTrue();
    }

    [Test]
    public async Task Strict_KeysHaveNoTombstones()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();

        await Assert.That(async () => await new AvroSharpSerializer<Order>(registry, StrictWriter).SerializeAsync(null!, Key(topic))).Throws<InvalidOperationException>();
        await Assert.That(() => new AvroSharpSerializer<string>(registry, StrictWriter).Serialize(null!, Key(topic))).Throws<InvalidOperationException>();
        await Assert.That(async () => await AvroSharpGeneric.CreateSerializer(registry, StrictWriter).SerializeAsync(AvroValue.Null, Key(topic))).Throws<InvalidOperationException>();
        await Assert.That(async () => await AvroSharpGeneric.CreateSerializer(registry, AvroSchema.Parse("\"string\""), StrictWriter).SerializeAsync(AvroValue.Null, Key(topic))).Throws<InvalidOperationException>();
        await Assert.That(async () => await new AvroSharpDeserializer<Order>(registry, StrictReader).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Key(topic))).Throws<InvalidOperationException>();
        await Assert.That(() => AvroSharpGeneric.CreateDeserializer(registry, config: StrictReader).Deserialize(ReadOnlySpan<byte>.Empty, isNull: true, Key(topic))).Throws<InvalidOperationException>();

        // Without Strict, a null key is no message body, as in Confluent's serde.
        await Assert.That(await new AvroSharpSerializer<Order>(registry).SerializeAsync(null!, Key(topic))).IsNull();
        await Assert.That(await new AvroSharpDeserializer<Order>(registry).DeserializeAsync(ReadOnlyMemory<byte>.Empty, isNull: true, Key(topic))).IsNull();
    }

    [Test]
    public async Task Strict_FromConfigurationPairs()
    {
        var config = new AvroSharpSerializerConfig([new("avro.serializer.tombstone.behavior", "Strict")]);

        await Assert.That(config.TombstoneBehavior).IsEqualTo(TombstoneBehavior.Strict);
        using var registry = new InMemorySchemaRegistry();
        await Assert.That(() => new AvroSharpSerializer<int>(registry, config)).Throws<ArgumentException>();
    }

    [Test]
    public async Task RemoveJavaProperties_RegistersTheSchemaWithout_AvroJavaString()
    {
        const string Plain = """{"type":"record","name":"R","namespace":"ns","fields":[{"name":"s","type":"string"},{"name":"a","type":{"type":"array","items":"string"}}]}""";
        const string Java = """{"type":"record","name":"R","namespace":"ns","fields":[{"name":"s","type":{"type":"string","avro.java.string":"String"}},{"name":"a","type":{"type":"array","items":{"type":"string","avro.java.string":"String"}}}]}""";
        var java = AvroSchema.Parse(Java);
        var record = new GenericRecord((RecordSchema)java) { ["s"] = "x", ["a"] = AvroValue.FromArray([AvroValue.FromString("y")]) };

        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        await registry.RegisterSchemaAsync(topic + "-value", Plain);
        var bytes = await AvroSharpGeneric.CreateSerializer(registry, java, new AvroSharpSerializerConfig { RemoveJavaProperties = true }).SerializeAsync(AvroValue.FromRecord(record), Value(topic));

        // The same version as the schema without the property, and the same data.
        await Assert.That(await registry.GetSubjectVersionsAsync(topic + "-value")).Count().IsEqualTo(1);
        await Assert.That((await registry.GetLatestSchemaAsync(topic + "-value")).SchemaString).DoesNotContain("avro.java.string");
        var read = await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(bytes, isNull: false, Value(topic));
        await Assert.That(read.AsRecord()["s"]).IsEqualTo(AvroValue.FromString("x"));

        // Without the option, the property is registered: another version.
        var other = Topic();
        await registry.RegisterSchemaAsync(other + "-value", Plain);
        await AvroSharpGeneric.CreateSerializer(registry, java).SerializeAsync(AvroValue.FromRecord(record), Value(other));
        await Assert.That(await registry.GetSubjectVersionsAsync(other + "-value")).Count().IsEqualTo(2);
    }
}
