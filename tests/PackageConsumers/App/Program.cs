// Writes and reads generated types with the packed packages: a zstandard container file, IAvroSerializable<T>, and
// the constructor's schema defaults. Exits with 1 on any difference.
using AvroSharp.Codecs;
using AvroSharp.Confluent;
using AvroSharp.Containers;
using AvroSharp.Serialization;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using consumer;

var order = new Order
{
    Id = 42,
    Status = Status.PAID,
    Hash = new Hash([1, 2, 3, 4]),
    Total = 12.34m,
    Day = new DateOnly(2026, 9, 29),
    At = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
    Ref = Guid.Parse("123e4567-e89b-12d3-a456-426614174000"),
    Tags = ["a", "b"],
    Attributes = new() { ["x"] = 1 },
};

var failures = new List<string>();

// The constructor gives every field its schema default, including the record default.
if (order.Shipping.City != "Budapest" || new Order().Status != Status.NEW)
{
    failures.Add("schema defaults");
}

var bytes = AvroSerializer.Serialize(order);
if (!AvroSerializer.Deserialize<Order>(bytes).ToAvroBytes().AsSpan().SequenceEqual(bytes))
{
    failures.Add("AvroSerializer round trip");
}

using var file = new MemoryStream();
using (var writer = AvroFileWriter.Create<Order>(file, new AvroFileWriterOptions { Codec = ZstandardCodec.Default, LeaveOpen = true }))
{
    writer.Write(order);
}

file.Position = 0;
using var reader = AvroFileReader.Open<Order>(file, new AvroFileReaderOptions { Codecs = AvroCodecs.All });
var read = reader.ReadAll().Single();
if (reader.Codec.Name != "zstandard" || !read.ToAvroBytes().AsSpan().SequenceEqual(bytes))
{
    failures.Add("zstandard container file");
}

// AvroSharp.Confluent and AvroSharp.KafkaFlow find the generated type through AvroTypes (registered when the
// assembly loads), and their Confluent and KafkaFlow dependencies resolve. Building them calls no registry, so no
// server is needed.
using (var registry = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = "http://localhost:8081" }))
{
    _ = new AvroSharpSerializer<Order>(registry);
    _ = new AvroSharpDeserializer<Order>(registry);
    _ = new ProducerBuilder<string, Order>(new ProducerConfig()).SetAvroSharpKeySerializer(registry).SetAvroSharpValueSerializer(registry);
    _ = new AvroSharp.KafkaFlow.AvroSharpKafkaFlowSerializer(registry);
    _ = new AvroSharp.KafkaFlow.AvroSharpMessageTypeResolver(registry, [typeof(Order)]);
}

// AvroSharp.Azure.SchemaRegistry loads with its Azure dependencies. A primitive has no name for the registry, which the
// serializer reports before any call to the service.
var azure = new AvroSharp.Azure.SchemaRegistry.AvroSharpSchemaRegistrySerializer(
    new Azure.Data.SchemaRegistry.SchemaRegistryClient("consumer.servicebus.windows.net", new NoCredential()), "group");
try
{
    azure.Serialize<Azure.Messaging.MessageContent, string>("text");
    failures.Add("Azure Schema Registry serializer");
}
catch (ArgumentException)
{
}

Console.WriteLine(failures.Count == 0 ? "App: ok" : "App failed: " + string.Join(", ", failures));
return failures.Count == 0 ? 0 : 1;

// A credential that is never asked for a token.
internal sealed class NoCredential : Azure.Core.TokenCredential
{
    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
}
