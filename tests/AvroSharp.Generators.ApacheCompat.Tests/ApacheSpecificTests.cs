using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avro.Specific;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;

namespace AvroSharp.Generators.ApacheCompat.Tests;

/// <summary>
/// With AvroSharpApacheCompatible, the generated types work with Apache.Avro's SpecificDatumWriter/Reader, and both
/// libraries produce the same bytes for the same instance (#12).
/// </summary>
public class ApacheSpecificTests
{
    private const int RandomIterations = 300;

    [Test]
    public async Task GeneratedTypes_ImplementApacheSpecificContracts()
    {
        await Assert.That(new shop.Order() is ISpecificRecord).IsTrue();
        await Assert.That(typeof(SpecificFixed).IsAssignableFrom(typeof(shop.Sku))).IsTrue();
        await Assert.That(typeof(shop.Order).GetProperty("Created")!.PropertyType).IsEqualTo(typeof(DateTime));
        await Assert.That(typeof(shop.Order).GetProperty("Amount")!.PropertyType).IsEqualTo(typeof(Avro.AvroDecimal));
        await Assert.That(new shop.Order().Schema.Fullname).IsEqualTo("shop.Order");
    }

    [Test]
    public async Task ApacheWriter_AndGeneratedWriter_ProduceTheSameBytes()
    {
        var order = CompatTestData.CreateOrder();

        var ours = order.ToAvroBytes();
        var apache = ApacheWrite(order);

        await Assert.That(Convert.ToHexString(apache)).IsEqualTo(Convert.ToHexString(ours));
    }

    [Test]
    public async Task ApacheReader_ReadsGeneratedBytes_IntoTheGeneratedType()
    {
        var order = CompatTestData.CreateOrder();
        var bytes = order.ToAvroBytes();

        var read = ApacheRead<shop.Order>(bytes);

        await Assert.That(read.CustomerName).IsEqualTo("Ada");
        await Assert.That(read.Status).IsEqualTo(shop.Status.@class);
        await Assert.That(read.Sku).IsEqualTo(order.Sku);
        await Assert.That(read.Amount).IsEqualTo(order.Amount);
        await Assert.That(Convert.ToHexString(read.ToAvroBytes())).IsEqualTo(Convert.ToHexString(bytes));
    }

    public static IEnumerable<string> GeneratedTypes() => ["shop.Order", "graph.Node", "crm.Customer", "Unnamespaced", "logical.Moments"];

    [Test]
    [MethodDataSource(nameof(GeneratedTypes))]
    public async Task RandomData_RoundTripsThroughBothLibraries_ToTheSameBytes(string typeName)
    {
        var checkedSamples = typeName switch
        {
            "shop.Order" => Check(shop.Order.AvroSharpSchema, shop.Order.FromAvroBytes, o => o.ToAvroBytes()),
            "graph.Node" => Check(graph.Node.AvroSharpSchema, graph.Node.FromAvroBytes, o => o.ToAvroBytes()),
            "crm.Customer" => Check(crm.Customer.AvroSharpSchema, crm.Customer.FromAvroBytes, o => o.ToAvroBytes()),
            "Unnamespaced" => Check(Unnamespaced.AvroSharpSchema, Unnamespaced.FromAvroBytes, o => o.ToAvroBytes()),
            // logical.Moments has a decimal on fixed, which Apache's specific writer cannot write (see ApacheWriter_CannotWriteDecimalOnFixed).
            _ => Check(logical.Moments.AvroSharpSchema, logical.Moments.FromAvroBytes, o => o.ToAvroBytes(), apacheCanWrite: false),
        };

        // RandomValues gives up on many deep recursive values (graph.Node), so not every seed yields a sample.
        await Assert.That(checkedSamples).IsGreaterThanOrEqualTo(20);
    }

    /// <summary>
    /// Known Apache.Avro 1.12.2 deviation: its specific writer cannot write a decimal on fixed. It converts the
    /// AvroDecimal from Get to a GenericFixed, then rejects that for not deriving from SpecificFixed (and given the
    /// fixed type instead, it fails to cast it to AvroDecimal). Its reader works: it reuses the field's fixed value and
    /// puts an AvroDecimal, which the generated Put accepts. AvroSharp writes these types fine.
    /// </summary>
    [Test]
    public async Task ApacheWriter_CannotWriteDecimalOnFixed()
    {
        var bytes = GenericDatumWriter.Create(logical.Moments.AvroSharpSchema)
            .WriteToArray(new RandomValues(1).TryCreate(logical.Moments.AvroSharpSchema)!.Value);

        var read = ApacheRead<logical.Moments>(bytes);

        await Assert.That(Convert.ToHexString(read.ToAvroBytes())).IsEqualTo(Convert.ToHexString(bytes));
        // Given the fixed type (what its reader reuses), Apache's writer fails to cast it to AvroDecimal.
        Assert.Throws<InvalidCastException>(() => ApacheWrite(read));
    }

    /// <summary>
    /// Apache.Avro reads a local-timestamp as a UTC instant shown in local time (Kind Local), and writes any DateTime
    /// with ToUniversalTime; the specification says a local timestamp has no time zone. The compatibility mode matches
    /// Apache, so the two libraries agree on every machine, not only on UTC ones.
    /// </summary>
    [Test]
    public async Task LocalTimestamps_FollowApache_InLocalTime()
    {
        var moments = logical.Moments.FromAvroBytes(GenericDatumWriter.Create(logical.Moments.AvroSharpSchema)
            .WriteToArray(new RandomValues(3).TryCreate(logical.Moments.AvroSharpSchema)!.Value));
        var apache = ApacheRead<logical.Moments>(moments.ToAvroBytes());

        await Assert.That(moments.LocalMillis.Kind).IsEqualTo(DateTimeKind.Local);
        await Assert.That(moments.LocalMillis).IsEqualTo(apache.LocalMillis);
        await Assert.That(moments.LocalMicros).IsEqualTo(apache.LocalMicros);
    }

    [Test]
    public async Task ApacheDecimal_WithAnotherScale_IsRejected()
    {
        var order = CompatTestData.CreateOrder();
        order.Amount = new Avro.AvroDecimal(new System.Numerics.BigInteger(5), 3);

        var ex = Assert.Throws<AvroException>(() => order.ToAvroBytes());

        await Assert.That(ex.Message).Contains("scale 3 is not the schema's scale 2");
    }

    /// <summary>
    /// Random generic data: Apache reads the bytes into the generated type, AvroSharp writes that instance again;
    /// AvroSharp reads the bytes into the generated type, Apache writes that instance again. All four byte arrays match.
    /// </summary>
    private static int Check<T>(AvroSharp.Schemas.AvroSchema schema, FromBytes<T> fromBytes, Func<T, byte[]> toBytes, bool apacheCanWrite = true)
        where T : ISpecificRecord, new()
    {
        var checkedSamples = 0;
        for (var seed = 0; seed < RandomIterations; seed++)
        {
            if (new RandomValues(seed).TryCreate(schema) is not { } value)
            {
                continue;
            }

            var bytes = GenericDatumWriter.Create(schema).WriteToArray(value);
            var viaApache = toBytes(ApacheRead<T>(bytes));
            var viaAvroSharp = apacheCanWrite ? ApacheWrite(fromBytes(bytes)) : toBytes(fromBytes(bytes));
            if (!viaApache.AsSpan().SequenceEqual(bytes) || !viaAvroSharp.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException(
                    $"Seed {seed}: bytes differ.\nValue:            {value}\nGeneric:          {Convert.ToHexString(bytes)}\nApache read:      {Convert.ToHexString(viaApache)}\nApache write:     {Convert.ToHexString(viaAvroSharp)}");
            }

            checkedSamples++;
        }

        return checkedSamples;
    }

    private static byte[] ApacheWrite<T>(T value)
        where T : ISpecificRecord
    {
        using var output = new MemoryStream();
        new SpecificDatumWriter<T>(value.Schema).Write(value, new Avro.IO.BinaryEncoder(output));
        return output.ToArray();
    }

    private static T ApacheRead<T>(byte[] bytes)
        where T : ISpecificRecord, new()
    {
        var schema = new T().Schema;
        using var input = new MemoryStream(bytes);
        var value = new SpecificDatumReader<T>(schema, schema).Read(default!, new Avro.IO.BinaryDecoder(input));
        return input.Position == input.Length
            ? value
            : throw new InvalidOperationException($"Apache left {input.Length - input.Position} byte(s) unread.");
    }

    private delegate T FromBytes<out T>(ReadOnlySpan<byte> data);
}
