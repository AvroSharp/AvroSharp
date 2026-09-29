// Writes and reads generated types with the packed packages: a zstandard container file, IAvroSerializable<T>, and
// the constructor's schema defaults. Exits with 1 on any difference.
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Serialization;
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
if (reader.Codec != "zstandard" || !read.ToAvroBytes().AsSpan().SequenceEqual(bytes))
{
    failures.Add("zstandard container file");
}

Console.WriteLine(failures.Count == 0 ? "App: ok" : "App failed: " + string.Join(", ", failures));
return failures.Count == 0 ? 0 : 1;
