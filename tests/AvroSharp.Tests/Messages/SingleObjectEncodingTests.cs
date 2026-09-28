using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Messages;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Messages;

/// <summary>Single-object encoding (M5, #32), checked against Java's <c>messageV1</c> test message.</summary>
public class SingleObjectEncodingTests
{
    private static readonly RecordSchema s_v1 = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"User","namespace":"test","fields":[{"name":"id","type":"int"},{"name":"name","type":"string"}]}""");

    private static readonly RecordSchema s_v2 = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"User","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"active","type":"boolean","default":true}]}""");

    [Test]
    public async Task JavasMessageV1_IsReadAndWrittenByteForByte()
    {
        var (schema, message) = MessageV1();

        var record = AvroMessageReader.CreateGeneric(new AvroSchemaStore(schema)).Read(message).AsRecord();
        var written = AvroMessage.ToArray(record, GenericDatumWriter.Create(schema));

        await Assert.That(record["id"].AsInt64()).IsEqualTo(42L);
        await Assert.That(record["name"].AsString()).IsEqualTo("Bill");
        await Assert.That(record["tags"].AsArray().Select(t => t.AsString()).SequenceEqual(new[] { "dog_lover", "cat_hater" })).IsTrue();
        await Assert.That(written.AsSpan().SequenceEqual(message)).IsTrue();
    }

    [Test]
    public async Task TheHeader_IsTheMarkerAndTheLittleEndianFingerprint()
    {
        var header = new byte[AvroMessage.HeaderLength];
        AvroMessage.WriteHeader(header, s_v1);

        await Assert.That(header[0]).IsEqualTo((byte)0xC3);
        await Assert.That(header[1]).IsEqualTo((byte)0x01);
        await Assert.That(BitConverter.ToInt64(header, 2)).IsEqualTo(s_v1.Fingerprint64);
        await Assert.That(AvroMessage.TryReadHeader(header, out var fingerprint)).IsTrue();
        await Assert.That(fingerprint).IsEqualTo(s_v1.Fingerprint64);
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 0xC3, 0x01, 1, 2, 3, 4, 5, 6, 7 })]
    [Arguments(new byte[] { 0xC3, 0x02, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [Arguments(new byte[] { 0x00, 0x01, 1, 2, 3, 4, 5, 6, 7, 8 })]
    public async Task DataWithoutAHeader_IsRejected(byte[] data)
    {
        var reader = AvroMessageReader.CreateGeneric(new AvroSchemaStore(s_v1));

        var ex = Assert.Throws<AvroDataException>(() => reader.Read(data));

        await Assert.That(AvroMessage.TryReadHeader(data, out _)).IsFalse();
        await Assert.That(ex.Message).Contains("not a single-object encoded Avro message");
    }

    [Test]
    public async Task AnUnknownFingerprint_IsNamedInTheError()
    {
        var message = AvroMessage.ToArray(User(s_v1, 1, "a"), GenericDatumWriter.Create(s_v1));

        var ex = Assert.Throws<AvroDataException>(() => AvroMessageReader.CreateGeneric(new AvroSchemaStore(s_v2)).Read(message));

        await Assert.That(ex.Message).Contains($"0x{s_v1.Fingerprint64:X16}");
    }

    [Test]
    public async Task BytesAfterTheObject_AreRejected()
    {
        var message = AvroMessage.ToArray(User(s_v1, 1, "a"), GenericDatumWriter.Create(s_v1)).Concat(new byte[] { 0 }).ToArray();

        var ex = Assert.Throws<AvroDataException>(() => AvroMessageReader.CreateGeneric(new AvroSchemaStore(s_v1)).Read(message));

        await Assert.That(ex.Message).Contains("1 bytes left");
    }

    [Test]
    public async Task MessagesOfEachVersion_AreResolvedToTheReaderSchema()
    {
        var old = AvroMessage.ToArray(User(s_v1, 7, "old"), GenericDatumWriter.Create(s_v1));
        var current = AvroMessage.ToArray(new GenericRecord(s_v2) { ["id"] = 8L, ["name"] = "new", ["active"] = false }, GenericDatumWriter.Create(s_v2));
        var reader = AvroMessageReader.CreateGeneric(new AvroSchemaStore(s_v1, s_v2), s_v2);

        var a = reader.Read(old).AsRecord();
        var b = reader.Read(current).AsRecord();

        await Assert.That(a.Schema).IsSameReferenceAs(s_v2);
        await Assert.That(a["id"].AsInt64()).IsEqualTo(7L);
        await Assert.That(a["active"].AsBoolean()).IsTrue();
        await Assert.That(b["name"].AsString()).IsEqualTo("new");
        await Assert.That(b["active"].AsBoolean()).IsFalse();
    }

    [Test]
    public async Task TheReadFunction_IsCreatedOncePerSchema()
    {
        var calls = 0;
        var writer = GenericDatumWriter.Create(s_v1);
        var reader = AvroMessageReader.Create<AvroValue>(new AvroSchemaStore(s_v1), schema =>
        {
            calls++;
            var datumReader = GenericDatumReader.Create(schema);
            return (ref r) => datumReader.Read(ref r);
        });

        for (var i = 0; i < 5; i++)
        {
            reader.Read(AvroMessage.ToArray(User(s_v1, i, "x"), writer));
        }

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task MessagesThatAlternateBetweenSchemas_UseEachSchemasReader()
    {
        var calls = 0;
        var reader = AvroMessageReader.Create<string>(new AvroSchemaStore(s_v1, s_v2), schema =>
        {
            calls++;
            var datumReader = GenericDatumReader.Create(schema);
            var version = ReferenceEquals(schema, s_v1) ? "v1" : "v2";
            return (ref r) => $"{version}:{datumReader.Read(ref r).AsRecord()["name"].AsString()}";
        });
        var old = AvroMessage.ToArray(User(s_v1, 1, "a"), GenericDatumWriter.Create(s_v1));
        var current = AvroMessage.ToArray(new GenericRecord(s_v2) { ["id"] = 2L, ["name"] = "b", ["active"] = true }, GenericDatumWriter.Create(s_v2));

        var read = new[] { old, current, current, old, current }.Select(m => reader.Read(m)).ToArray();

        await Assert.That(string.Join(",", read)).IsEqualTo("v1:a,v2:b,v2:b,v1:a,v2:b");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task AStoreThatReturnsTheWrongSchema_IsReported()
    {
        var message = AvroMessage.ToArray(User(s_v1, 1, "a"), GenericDatumWriter.Create(s_v1));

        var ex = Assert.Throws<AvroException>(() => AvroMessageReader.CreateGeneric(new WrongStore()).Read(message));

        await Assert.That(ex.Message).Contains("schema store returned");
    }

    private static (AvroSchema Schema, byte[] Message) MessageV1() => (
        AvroSchema.Parse(File.ReadAllText(TestData.PathOf("apache-avro/messageV1/test_schema.avsc"))),
        File.ReadAllBytes(TestData.PathOf("apache-avro/messageV1/test_message.bin")));

    private static GenericRecord User(RecordSchema schema, int id, string name) => new(schema) { ["id"] = id, ["name"] = name };

    private sealed class WrongStore : IAvroSchemaStore
    {
        public AvroSchema? GetSchema(long fingerprint) => s_v2;
    }
}
