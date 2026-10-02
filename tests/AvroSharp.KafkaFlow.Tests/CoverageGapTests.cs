using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using AvroSharp.Confluent.Tests;
using Confluent.SchemaRegistry;
using KafkaFlow;
using test.shop;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.KafkaFlow.Tests;

/// <summary>Paths of AvroSharp.KafkaFlow the rc.2 review found untested (#215).</summary>
public class CoverageGapTests
{
    [Test]
    public async Task Bodies_AreRead_FromEveryKindOfStream()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var message = await KafkaFlowSerializerTests.Serialize(new AvroSharpKafkaFlowSerializer(registry), NewOrder(), topic);
        var deserializer = new AvroSharpKafkaFlowDeserializer(registry);
        var context = new SerializerContext(topic);
        byte[] padded = [9, 9, 9, .. message];

        // A MemoryStream whose buffer is exposed, read from its position; a seekable stream that isn't one; a stream
        // that can't seek.
        using var exposed = new MemoryStream(padded, 0, padded.Length, writable: false, publiclyVisible: true) { Position = 3 };
        using var buffered = new BufferedStream(new MemoryStream(message));
        using var forward = new ForwardOnlyStream(message);

        foreach (var input in new Stream[] { exposed, buffered, forward })
        {
            await KafkaFlowSerializerTests.AssertIsNewOrder((Order)await deserializer.DeserializeAsync(input, typeof(Order), context));
        }
    }

    [Test]
    public async Task Resolver_FetchesEachSchemaOnce_AndResolvesConcurrently()
    {
        using var registry = new InMemorySchemaRegistry();
        var topic = Topic();
        var serializer = new AvroSharpKafkaFlowSerializer(registry, new AvroSharpSerializerConfig { SubjectNameStrategy = SubjectNameStrategy.TopicRecord });
        var order = await KafkaFlowSerializerTests.Serialize(serializer, NewOrder(), topic);
        var cart = await KafkaFlowSerializerTests.Serialize(serializer, new Cart { Owner = "Ada" }, topic);
        var resolver = new AvroSharpMessageTypeResolver(registry, [typeof(Order), typeof(Cart)]);
        var fetchesBefore = registry.GetSchemaCalls;

        var types = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () => await resolver.OnConsumeAsync(new ConsumedMessage(i % 2 == 0 ? order : cart)))));

        await Assert.That(types.Where((t, i) => t != (i % 2 == 0 ? typeof(Order) : typeof(Cart)))).IsEmpty();
        await Assert.That(registry.GetSchemaCalls - fetchesBefore).IsLessThanOrEqualTo(2 * 64).Because("concurrent first fetches may race");
        var once = new AvroSharpMessageTypeResolver(registry, [typeof(Order)]);
        var before = registry.GetSchemaCalls;
        await once.OnConsumeAsync(new ConsumedMessage(order));
        await once.OnConsumeAsync(new ConsumedMessage(order));
        await Assert.That(registry.GetSchemaCalls - before).IsEqualTo(1);
    }

    [Test]
    [Arguments("""{"type":"record","name":"a.b.Dotted","fields":[]}""", "a.b.Dotted")]
    [Arguments("""{"type":"record","name":"NoSpace","fields":[]}""", "NoSpace")]
    [Arguments("""{"type":"record","name":"EmptySpace","namespace":"","fields":[]}""", "EmptySpace")]
    [Arguments("""{"type":"record","name":"Dotted.Wins","namespace":"ignored","fields":[]}""", "Dotted.Wins")]
    [Arguments("""{"type":"error","name":"Failure","namespace":"x.y","fields":[]}""", "x.y.Failure")]
    public async Task RecordNames_FollowTheSpecification(string schema, string fullName)
    {
        using var registry = new InMemorySchemaRegistry();
        var id = await registry.RegisterSchemaAsync(Topic() + "-value", new Schema(schema, SchemaType.Avro));
        var resolver = new AvroSharpMessageTypeResolver(registry, [typeof(Order)]);

        await Assert.That(async () => await resolver.OnConsumeAsync(new ConsumedMessage([0, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id])))
            .Throws<InvalidOperationException>().WithMessageContaining($"the record {fullName} (schema {id})", StringComparison.Ordinal);
    }

    [Test]
    public async Task ALongMessage_WithoutTheZeroByte_IsNotConfluentFraming()
    {
        using var registry = new InMemorySchemaRegistry();
        var resolver = new AvroSharpMessageTypeResolver(registry, [typeof(Order)]);

        await Assert.That(async () => await resolver.OnConsumeAsync(new ConsumedMessage([1, 0, 0, 0, 1, 2, 3])))
            .Throws<InvalidOperationException>().WithMessageContaining("framing", StringComparison.Ordinal);
    }

    /// <summary>A stream that can only be read forward, as a network stream.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
