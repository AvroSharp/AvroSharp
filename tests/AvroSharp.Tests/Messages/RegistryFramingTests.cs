using System;
using System.Collections.Generic;
using System.IO;
#if NET
using System.IO.Compression;
#endif
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Messages;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Messages;

/// <summary>Schema-registry wire framing (#81): Confluent, Apicurio and AWS Glue layouts, lookups and hostile input.</summary>
public class RegistryFramingTests
{
    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(
        """{"type":"record","name":"User","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"}]}""");

    private static readonly Guid s_guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    public static IEnumerable<(string Framing, bool Guid)> Framings() =>
    [
        ("Confluent", false), ("ConfluentGuid", true), ("Apicurio", false), ("Apicurio8Byte", false), ("AwsGlue", true), ("AwsGlueCompressed", true),
    ];

    [Test]
    [MethodDataSource(nameof(Framings))]
    public async Task Messages_RoundTrip_SynchronouslyAndAsynchronously(string framingName, bool guid)
    {
        var framing = FramingNamed(framingName);
        var id = guid ? AvroSchemaId.FromGuid(s_guid) : AvroSchemaId.FromNumber(string.Equals(framingName, "Apicurio8Byte", StringComparison.Ordinal) ? 1L << 40 : 42);
        var store = new AvroSchemaIdStore();
        store.Add(id, s_schema);
        var value = User(7, new string('x', 300));

        var message = AvroRegistryMessage.ToArray(framing, id, value, GenericDatumWriter.Create(s_schema));
        var reader = AvroRegistryMessageReader.CreateGeneric(framing, store);

        await Assert.That(framing.TryReadHeader(message, out var read)).IsTrue();
        await Assert.That(read).IsEqualTo(id);
        await Assert.That(reader.Read(message).Equals(value)).IsTrue();
        await Assert.That((await reader.ReadAsync(message)).Equals(value)).IsTrue();
        // Compression shrinks the repetitive name; the others carry the plain Avro data after the header.
        var plain = GenericDatumWriter.Create(s_schema).WriteToArray(value);
        await Assert.That(framing.Compresses ? message.Length < plain.Length : message.AsSpan(framing.HeaderLength).SequenceEqual(plain)).IsTrue();
    }

    [Test]
    public async Task Headers_HaveTheRegistriesLayouts()
    {
        var number = AvroSchemaId.FromNumber(0x01020304);
        var guid = AvroSchemaId.FromGuid(s_guid);
        var guidBytes = "00112233445566778899AABBCCDDEEFF";

        await Assert.That(Header(AvroRegistryFraming.Confluent, number)).IsEqualTo("0001020304");
        await Assert.That(Header(AvroRegistryFraming.Apicurio, number)).IsEqualTo("0001020304");
        await Assert.That(Header(AvroRegistryFraming.Apicurio8Byte, AvroSchemaId.FromNumber(0x0102030405060708))).IsEqualTo("000102030405060708");
        await Assert.That(Header(AvroRegistryFraming.ConfluentGuid, guid)).IsEqualTo("01" + guidBytes);
        await Assert.That(Header(AvroRegistryFraming.AwsGlue, guid)).IsEqualTo("0300" + guidBytes);
        await Assert.That(Header(AvroRegistryFraming.AwsGlueCompressed, guid)).IsEqualTo("0305" + guidBytes);
    }

    [Test]
    public async Task IdsOfTheWrongKindOrSize_AreRejectedWhenWriting()
    {
        var buffer = new byte[32];

        Assert.Throws<ArgumentException>(() => AvroRegistryFraming.Confluent.WriteHeader(buffer, AvroSchemaId.FromGuid(s_guid)));
        Assert.Throws<ArgumentException>(() => AvroRegistryFraming.AwsGlue.WriteHeader(buffer, AvroSchemaId.FromNumber(1)));
        var tooBig = Assert.Throws<ArgumentException>(() => AvroRegistryFraming.Confluent.WriteHeader(buffer, AvroSchemaId.FromNumber(1L << 32)));

        await Assert.That(tooBig.Message).Contains("does not fit");
    }

    [Test]
    [Arguments(new byte[0], "not a Confluent message")]
    [Arguments(new byte[] { 0x00, 0x00, 0x00 }, "not a Confluent message")]
    [Arguments(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x2A, 0x00 }, "not a Confluent message")]
    [Arguments(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00 }, "No schema with ID 7")]
    [Arguments(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x2A, 0x02 }, "end of Avro data")]
    [Arguments(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x2A, 0x02, 0x02, 0x41, 0x00 }, "1 bytes left")]
    public async Task MalformedConfluentMessages_AreRejected(byte[] message, string error)
    {
        var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, StoreWith(AvroSchemaId.FromNumber(42)));

        var ex = Assert.Throws<AvroDataException>(() => reader.Read(message));

        await Assert.That(ex.Message).Contains(error);
    }

    [Test]
    public async Task AGlueHeaderWithAnUnknownCompressionByte_IsRejected()
    {
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlue, AvroSchemaId.FromGuid(s_guid), User(1, "a"), GenericDatumWriter.Create(s_schema));
        message[1] = 0x04;

        var ex = Assert.Throws<AvroDataException>(() => AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, StoreWith(AvroSchemaId.FromGuid(s_guid))).Read(message));

        await Assert.That(ex.Message).Contains("not a AwsGlue message");
    }

    [Test]
    [Arguments("header")]
    [Arguments("checksum")]
    [Arguments("data")]
    public async Task CorruptZlibData_IsRejected(string damage)
    {
        var id = AvroSchemaId.FromGuid(s_guid);
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlueCompressed, id, User(1, new string('y', 200)), GenericDatumWriter.Create(s_schema));
        switch (damage)
        {
            case "header":
                message[18] = 0x79;
                break;
            case "checksum":
                message[^1] ^= 0xFF;
                break;
            default:
                message[21] ^= 0xFF;
                break;
        }

        var ex = Assert.Throws<AvroDataException>(() => AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, StoreWith(id)).Read(message));

        await Assert.That(ex.Message).Contains("compressed data");
    }

    [Test]
    public async Task ACompressedPayloadExpandingPastTheLimit_IsRejected()
    {
        // A 1 MiB bytes value of zeros compresses to about a kilobyte.
        var schema = AvroSchema.Parse("\"bytes\"");
        var id = AvroSchemaId.FromGuid(s_guid);
        var store = new AvroSchemaIdStore();
        store.Add(id, schema);
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlueCompressed, id, (AvroValue)new byte[1 << 20], GenericDatumWriter.Create(schema));

        var limited = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, store, options: new AvroRegistryReaderOptions { MaxPayloadLength = 64 * 1024 });
        var ex = Assert.Throws<AvroDataException>(() => limited.Read(message));

        await Assert.That(message.Length).IsLessThan(8 * 1024);
        await Assert.That(ex.Message).Contains("larger than the limit");
        await Assert.That(AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, store).Read(message).AsBytes().Length).IsEqualTo(1 << 20);
    }

    [Test]
    public async Task ReadAsync_FetchesAnUnknownIdOnce_AndReadUsesTheCache()
    {
        var resolver = new CountingResolver();
        var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, resolver);
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(5), User(1, "a"), GenericDatumWriter.Create(s_schema));

        var syncBeforeFetch = Assert.Throws<AvroDataException>(() => reader.Read(message));
        await reader.ReadAsync(message);
        await reader.ReadAsync(message);
        reader.Read(message);

        await Assert.That(syncBeforeFetch.Message).Contains("No schema with ID 5");
        await Assert.That(resolver.AsyncCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAsync_WhenTheResolverFindsNoSchema_IsRejected_AndNotCached()
    {
        var resolver = new ScriptedResolver(_ => null);
        var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, resolver);
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(5), User(1, "a"), GenericDatumWriter.Create(s_schema));

        var first = await Assert.ThrowsAsync<AvroDataException>(async () => await reader.ReadAsync(message));
        var second = await Assert.ThrowsAsync<AvroDataException>(async () => await reader.ReadPayloadAsync(AvroSchemaId.FromNumber(5), message.AsMemory(AvroRegistryFraming.Confluent.HeaderLength)));

        await Assert.That(first!.Message).IsEqualTo("No schema with ID 5 is known to the resolver.");
        await Assert.That(second!.Message).IsEqualTo("No schema with ID 5 is known to the resolver.");
        await Assert.That(resolver.AsyncCalls).IsEqualTo(2);
    }

    [Test]
    public async Task ReadAsync_PassesTheTokenToTheFetch_AndACancelledFetchIsNotCached()
    {
        // The first fetch waits until it is cancelled; the next one finds the schema.
        var resolver = new ScriptedResolver(call => call == 1 ? null : s_schema, waitForCancellation: call => call == 1);
        var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, resolver);
        var message = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(5), User(1, "a"), GenericDatumWriter.Create(s_schema));
        using var cancellation = new CancellationTokenSource();

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.ReadAsync(message, cancellation.Token));
        await Assert.That(await reader.ReadAsync(message)).IsEqualTo(User(1, "a"));
        await Assert.That(resolver.AsyncCalls).IsEqualTo(2);
    }

    [Test]
    public async Task TheReadFunction_IsCreatedOncePerId_AcrossAlternatingIds()
    {
        var a = AvroSchemaId.FromNumber(1);
        var b = AvroSchemaId.FromNumber(2);
        var store = StoreWith(a);
        store.Add(b, s_schema);
        var calls = 0;
        var reader = AvroRegistryMessageReader.Create<AvroValue>(AvroRegistryFraming.Confluent, store, schema =>
        {
            calls++;
            var datum = GenericDatumReader.Create(schema);
            return (ref r) => datum.Read(ref r);
        });
        var writer = GenericDatumWriter.Create(s_schema);

        for (var i = 0; i < 6; i++)
        {
            reader.Read(AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, i % 2 == 0 ? a : b, User(i, "x"), writer));
        }

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ConfluentHeaderIds_AreEncodedAsVersionOneGuids_AndReadWithReadPayload()
    {
        var header = ConfluentSchemaIdHeader.Encode(AvroSchemaId.FromGuid(s_guid));
        var payload = GenericDatumWriter.Create(s_schema).WriteToArray(User(3, "h"));
        var reader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.ConfluentGuid, StoreWith(AvroSchemaId.FromGuid(s_guid)));

        await Assert.That(Convert.ToHexString(header)).IsEqualTo("0100112233445566778899AABBCCDDEEFF");
        await Assert.That(ConfluentSchemaIdHeader.TryDecode(header, out var id) && id.Guid == s_guid).IsTrue();
        await Assert.That(Assert.Throws<ArgumentException>(() => ConfluentSchemaIdHeader.Encode(AvroSchemaId.FromNumber(5))).Message).StartsWith("Confluent's schema ID header carries a GUID; the ID is a number.");
        await Assert.That(ConfluentSchemaIdHeader.TryDecode(header.AsSpan(0, 16), out _)).IsFalse();
        await Assert.That(reader.ReadPayload(id, payload).AsRecord()["name"].AsString()).IsEqualTo("h");
        await Assert.That((await reader.ReadPayloadAsync(id, payload)).AsRecord()["id"].AsInt64()).IsEqualTo(3L);
        await Assert.That(ConfluentSchemaIdHeader.ValueHeaderName).IsEqualTo("__value_schema_id");
    }

    [Test]
    public async Task SchemaIds_CompareByKindAndValue()
    {
        await Assert.That(AvroSchemaId.FromNumber(1)).IsEqualTo(AvroSchemaId.FromNumber(1));
        await Assert.That(AvroSchemaId.FromNumber(0) == AvroSchemaId.FromGuid(Guid.Empty)).IsFalse();
        await Assert.That(AvroSchemaId.FromGuid(s_guid).ToString()).IsEqualTo("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Throws<InvalidOperationException>(() => _ = AvroSchemaId.FromNumber(1).Guid);
        Assert.Throws<InvalidOperationException>(() => _ = AvroSchemaId.FromGuid(s_guid).Number);
    }

#if NET
    [Test]
    public async Task GlueZlibData_IsTheFormatZLibStreamReadsAndWrites()
    {
        var id = AvroSchemaId.FromGuid(s_guid);
        var value = User(9, new string('z', 500));
        var plain = GenericDatumWriter.Create(s_schema).WriteToArray(value);

        // Ours, read by the BCL's zlib implementation.
        var ours = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlueCompressed, id, value, GenericDatumWriter.Create(s_schema));
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(ours, 18, ours.Length - 18), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        // The BCL's, read by ours.
        using var deflated = new MemoryStream();
        deflated.Write([0x03, 0x05]);
        deflated.Write(ours.AsSpan(2, 16));
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        var read = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, StoreWith(id)).Read(deflated.ToArray());

        await Assert.That(inflated.ToArray().AsSpan().SequenceEqual(plain)).IsTrue();
        await Assert.That(read.Equals(value)).IsTrue();
    }
#endif

    /// <summary>
    /// Messages written with AWS Glue's own library (TestData/aws-glue/GlueMessages.java), plain and zlib-compressed:
    /// an external oracle for the Glue framings, which had only the documentation (#133).
    /// </summary>
    [Test]
    public async Task GluesOwnMessages_AreRead_AndThePlainOneIsWrittenByteForByte()
    {
        var schema = AvroSchema.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "aws-glue", "reading.avsc")));
        var id = AvroSchemaId.FromGuid(Guid.Parse("b7b4a7f0-9b8c-4a1e-8d2f-0123456789ab"));
        var store = new AvroSchemaIdStore();
        store.Add(id, schema);
        var plain = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "aws-glue", "plain.bin"));
        var zlib = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "aws-glue", "zlib.bin"));

        var fromPlain = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlue, store).Read(plain).AsRecord();
        var fromZlib = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.AwsGlueCompressed, store).Read(zlib).AsRecord();

        await Assert.That(fromPlain["id"].AsInt64()).IsEqualTo(1234567890123L);
        await Assert.That(fromPlain["sensor"].AsString()).IsEqualTo("north-gate");
        await Assert.That(fromPlain["values"].AsArray().Count).IsEqualTo(64);
        await Assert.That(fromZlib.Equals(fromPlain)).IsTrue();

        // Written by AvroSharp, the plain message is Glue's byte for byte.
        var ours = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlue, id, (AvroValue)fromPlain, GenericDatumWriter.Create(schema));
        await Assert.That(Convert.ToHexString(ours)).IsEqualTo(Convert.ToHexString(plain));

#if NET
        // The compressed one has Glue's header, and data that inflates to Glue's payload (zlib output itself may differ).
        var compressed = AvroRegistryMessage.ToArray(AvroRegistryFraming.AwsGlueCompressed, id, (AvroValue)fromPlain, GenericDatumWriter.Create(schema));
        using var inflated = new MemoryStream();
        using (var stream = new ZLibStream(new MemoryStream(compressed, 18, compressed.Length - 18), CompressionMode.Decompress))
        {
            stream.CopyTo(inflated);
        }

        await Assert.That(Convert.ToHexString(compressed.AsSpan(0, 18))).IsEqualTo(Convert.ToHexString(zlib.AsSpan(0, 18)));
        await Assert.That(Convert.ToHexString(inflated.ToArray())).IsEqualTo(Convert.ToHexString(plain.AsSpan(18)));
#endif
    }

    private static AvroRegistryFraming FramingNamed(string name) => name switch
    {
        "Confluent" => AvroRegistryFraming.Confluent,
        "ConfluentGuid" => AvroRegistryFraming.ConfluentGuid,
        "Apicurio" => AvroRegistryFraming.Apicurio,
        "Apicurio8Byte" => AvroRegistryFraming.Apicurio8Byte,
        "AwsGlue" => AvroRegistryFraming.AwsGlue,
        _ => AvroRegistryFraming.AwsGlueCompressed,
    };

    private static string Header(AvroRegistryFraming framing, AvroSchemaId id)
    {
        var header = new byte[framing.HeaderLength];
        framing.WriteHeader(header, id);
        return Convert.ToHexString(header);
    }

    private static AvroSchemaIdStore StoreWith(AvroSchemaId id)
    {
        var store = new AvroSchemaIdStore();
        store.Add(id, s_schema);
        return store;
    }

    private static AvroValue User(long id, string name) => new GenericRecord(s_schema) { ["id"] = id, ["name"] = name };

    /// <summary>Answers fetches asynchronously with what <c>answer</c> gives for the call number (from 1), optionally waiting for cancellation first.</summary>
    private sealed class ScriptedResolver(Func<int, AvroSchema?> answer, Func<int, bool>? waitForCancellation = null) : IAvroSchemaIdResolver
    {
        public int AsyncCalls { get; private set; }

        public AvroSchema? GetSchema(AvroSchemaId id) => null;

        public async ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default)
        {
            var call = ++AsyncCalls;
            if (waitForCancellation?.Invoke(call) == true)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await Task.Yield();
            return answer(call);
        }
    }

    private sealed class CountingResolver : IAvroSchemaIdResolver
    {
        private readonly AvroSchemaIdStore _fetched = new();

        public int AsyncCalls { get; private set; }

        public AvroSchema? GetSchema(AvroSchemaId id) => _fetched.GetSchema(id);

        public async ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            await Task.Yield();
            _fetched.Add(id, s_schema);
            return s_schema;
        }
    }
}
