using System;
using System.Threading.Tasks;
using AvroSharp.IO;
using AvroSharp.Messages;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Generated types as single-object encoded messages (#32).</summary>
public class MessageTests
{
    [Test]
    public async Task AGeneratedRecord_RoundTripsAsAMessage()
    {
        var order = TestData.CreateOrder();

        var message = AvroMessage.ToArray(order, shop.Order.Schema, shop.Order.Write);
        var reader = AvroMessageReader.Create<shop.Order>(new AvroSchemaStore(shop.Order.Schema), writerSchema => writerSchema == shop.Order.Schema
            ? shop.Order.Read
            : (ref AvroReader r) => shop.Order.Read(ref r, writerSchema));
        var back = reader.Read(message);

        await Assert.That(AvroMessage.TryReadHeader(message, out var fingerprint)).IsTrue();
        await Assert.That(fingerprint).IsEqualTo(shop.Order.Schema.Fingerprint64);
        await Assert.That(message.AsSpan(AvroMessage.HeaderLength).SequenceEqual(order.ToAvroBytes())).IsTrue();
        await Assert.That(back.ToAvroBytes().AsSpan().SequenceEqual(order.ToAvroBytes())).IsTrue();
    }
}
