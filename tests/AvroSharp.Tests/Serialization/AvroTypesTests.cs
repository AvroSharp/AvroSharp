using System;
using System.Threading.Tasks;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Tests.Serialization;

/// <summary>AvroTypes (#31): lookup of a type's schema and serializers without reflection, and the primitives.</summary>
public class AvroTypesTests
{
    [Test]
    [Arguments(typeof(bool), "\"boolean\"")]
    [Arguments(typeof(int), "\"int\"")]
    [Arguments(typeof(long), "\"long\"")]
    [Arguments(typeof(float), "\"float\"")]
    [Arguments(typeof(double), "\"double\"")]
    [Arguments(typeof(string), "\"string\"")]
    [Arguments(typeof(byte[]), "\"bytes\"")]
    public async Task Primitives_AreRegistered(Type type, string schema)
    {
        var found = AvroTypes.TryGet(type, out var info);

        await Assert.That(found).IsTrue();
        await Assert.That(info!.Type).IsEqualTo(type);
        await Assert.That(info.Schema.CanonicalForm).IsEqualTo(schema);
    }

    [Test]
    public async Task APrimitive_RoundTrips_AndReadsAPromotedWriterSchema()
    {
        var info = AvroTypes.Get<long>();
        var buffer = new byte[16];
        var writer = new AvroWriter(buffer);
        AvroTypes.Get<int>().Write(ref writer, 300);
        var length = (int)writer.BytesWritten;

        var reader = new AvroReader(buffer.AsSpan(0, length));
        var promoted = info.ReadFor(AvroSchema.Parse("\"int\""))(ref reader);
        var boxedReader = new AvroReader(buffer.AsSpan(0, length));
        var boxed = info.ReadObject(ref boxedReader, AvroSchema.Parse("\"int\""));

        await Assert.That(promoted).IsEqualTo(300L);
        await Assert.That(boxed).IsEqualTo(300L);
        Assert.Throws<AvroSchemaException>(() => info.ReadFor(AvroSchema.Parse("\"string\"")));
    }

    [Test]
    public async Task ARegistration_IsFoundByTypeArgumentAndByType_AndTheFirstOneWins()
    {
        var calls = 0;
        var first = new AvroTypeInfo<Marker>(() => { calls++; return AvroSchema.Parse("\"null\""); }, static (ref _, _) => { }, static (ref _) => new Marker());
        var second = new AvroTypeInfo<Marker>(() => AvroSchema.Parse("\"int\""), static (ref _, _) => { }, static (ref _) => new Marker());

        AvroTypes.Register(first);
        AvroTypes.Register(second);

        await Assert.That(ReferenceEquals(AvroTypes.Get<Marker>(), first)).IsTrue();
        await Assert.That(AvroTypes.TryGet(typeof(Marker), out var boxed) && ReferenceEquals(boxed, first)).IsTrue();
        await Assert.That(first.Schema.CanonicalForm).IsEqualTo("\"null\"");
        await Assert.That(first.Schema.CanonicalForm).IsEqualTo("\"null\"");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AnUnknownType_IsNotFound()
    {
        await Assert.That(AvroTypes.TryGet<Unregistered>(out _)).IsFalse();
        await Assert.That(AvroTypes.TryGet(typeof(Unregistered), out _)).IsFalse();
        var ex = Assert.Throws<InvalidOperationException>(() => AvroTypes.Get<Unregistered>());
        await Assert.That(ex.Message).Contains("has no Avro serializers");
    }

    [Test]
    public async Task Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => AvroTypes.Register<Marker>(null!));
        Assert.Throws<ArgumentNullException>(() => AvroTypes.TryGet(null!, out _));
        Assert.Throws<ArgumentNullException>(() => new AvroTypeInfo<Marker>(null!, static (ref _, _) => { }, static (ref _) => new Marker()));
        await Assert.That(AvroTypes.Get<int>().ReadFor(AvroSchema.Parse("\"int\""))).IsNotNull();
    }

    private sealed class Marker;

    // A struct, since nothing creates one.
    private struct Unregistered;
}
