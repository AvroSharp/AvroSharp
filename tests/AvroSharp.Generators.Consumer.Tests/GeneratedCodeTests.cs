using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;
using AvroSharp.IO;
using AvroSharp.Schemas;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// The code the generator emitted for Schemas/*.avsc, compiled by the SDK like any consumer project. The generated
/// serializers must produce exactly the bytes of the generic writer (and so of Apache.Avro) for the same data.
/// </summary>
public class GeneratedCodeTests
{
    private const int RandomIterations = 400;

    [Test]
    public async Task EveryType_RoundTrips_AndMatchesTheGenericWriter()
    {
        var order = CreateOrder();

        var bytes = order.ToAvroBytes();
        var back = shop.Order.FromAvroBytes(bytes);

        await Assert.That(back.ToAvroBytes().AsSpan().SequenceEqual(bytes)).IsTrue();
        var generic = GenericDatumReader.Create(shop.Order.Schema).Read(bytes);
        await Assert.That(GenericDatumWriter.Create(shop.Order.Schema).WriteToArray(generic).AsSpan().SequenceEqual(bytes)).IsTrue();

        await Assert.That(back.CustomerName).IsEqualTo("Ada");
        await Assert.That(back.Status).IsEqualTo(shop.Status.@class);
        await Assert.That(back.Sku).IsEqualTo(order.Sku);
        await Assert.That(back.Counters).IsEquivalentTo(order.Counters);
        await Assert.That(back.Weights).IsEquivalentTo(order.Weights);
        await Assert.That(back.Lines[1].Note).IsEqualTo("fragile");
        await Assert.That(back.Groups["g"]).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(back.Discount).IsNull();
        await Assert.That(back.Priority).IsEqualTo(7);
        await Assert.That(back.Extra).IsTypeOf<List<long>>();
        await Assert.That(back.Order_).IsEqualTo(99);
        await Assert.That(back.Schema_).IsEqualTo("s");
    }

    [Test]
    public async Task ApacheAvro_ReadsGeneratedBytes_AndWritesThemBackIdentically()
    {
        var bytes = CreateOrder().ToAvroBytes();

        var apacheSchema = ApacheSchema.Parse(shop.Order.SchemaJson);
        using var input = new MemoryStream(bytes);
        var apacheValue = new ApacheReader(apacheSchema, apacheSchema).Read(null!, new Avro.IO.BinaryDecoder(input));
        using var output = new MemoryStream();
        new ApacheWriter(apacheSchema).Write(apacheValue, new Avro.IO.BinaryEncoder(output));

        await Assert.That(input.Position).IsEqualTo(input.Length);
        await Assert.That(output.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    public static IEnumerable<string> GeneratedTypes() => ["shop.Order", "graph.Node", "crm.Customer", "Unnamespaced"];

    [Test]
    [MethodDataSource(nameof(GeneratedTypes))]
    public async Task RandomGenericData_IsReadAndWrittenBackIdentically(string typeName)
    {
        var (schema, roundTrip) = typeName switch
        {
            "shop.Order" => (shop.Order.Schema, (Func<byte[], byte[]>)(b => shop.Order.FromAvroBytes(b).ToAvroBytes())),
            "graph.Node" => (graph.Node.Schema, b => graph.Node.FromAvroBytes(b).ToAvroBytes()),
            "crm.Customer" => (crm.Customer.Schema, b => crm.Customer.FromAvroBytes(b).ToAvroBytes()),
            _ => (Generated.Default.Unnamespaced.Schema, b => Generated.Default.Unnamespaced.FromAvroBytes(b).ToAvroBytes()),
        };

        var checkedSamples = 0;
        for (var seed = 0; seed < RandomIterations; seed++)
        {
            if (new RandomValues(seed).TryCreate(schema) is not { } value)
            {
                continue;
            }

            var bytes = GenericDatumWriter.Create(schema).WriteToArray(value);
            var again = roundTrip(bytes);
            if (!again.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException($"Seed {seed}: bytes differ.\nValue: {value}\nGeneric:   {Convert.ToHexString(bytes)}\nGenerated: {Convert.ToHexString(again)}");
            }

            checkedSamples++;
        }

        // RandomValues gives up on many deep recursive values (graph.Node), so not every seed yields a sample.
        await Assert.That(checkedSamples).IsGreaterThanOrEqualTo(20);
    }

    [Test]
    public async Task TruncatedInput_IsReportedAsAvroDataException()
    {
        var bytes = CreateOrder().ToAvroBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var prefix = bytes.AsSpan(0, length).ToArray();
            Assert.Throws<AvroDataException>(() => shop.Order.FromAvroBytes(prefix));
        }

        await Assert.That(bytes.Length).IsGreaterThan(50);
    }

    [Test]
    public async Task DeepRecursion_IsRejected_WhenWritingAndReading()
    {
        var head = new graph.Node();
        var node = head;
        for (var i = 0; i < 200; i++)
        {
            node.Next = new graph.Node { Value = i };
            node = node.Next;
        }

        var writeError = Assert.Throws<AvroException>(() => head.ToAvroBytes());
        await Assert.That(writeError.Message).Contains("nested more than 128");

        // The generic writer, with a higher limit, produces the deep input.
        var generic = GenericDatumReader.Create(graph.Node.Schema, new GenericDatumReaderOptions { MaxDepth = 1000 })
            .Read(DeepNodeBytes(200));
        var bytes = GenericDatumWriter.Create(graph.Node.Schema, new GenericDatumWriterOptions { MaxDepth = 1000 }).WriteToArray(generic);
        var readError = Assert.Throws<AvroDataException>(() => graph.Node.FromAvroBytes(bytes));
        await Assert.That(readError.Message).Contains("nested more than 128");
    }

    [Test]
    public async Task HugeBlockCounts_AreRejectedBeforeAllocating()
    {
        // Node: value 0, next null, then children with a block count of 1,000,000,000.
        byte[] children = [0x00, 0x00, .. Varint(1_000_000_000), 0x00];
        var childrenError = Assert.Throws<AvroDataException>(() => graph.Node.FromAvroBytes(children));
        await Assert.That(childrenError.Message).Contains("larger than the remaining input");

        // Zero-size items (nulls) cannot be bounded by the input size, so they have a budget.
        byte[] nulls = [0x00, 0x00, 0x00, .. Varint(100_000), 0x00];
        var nullsError = Assert.Throws<AvroDataException>(() => graph.Node.FromAvroBytes(nulls));
        await Assert.That(nullsError.Message).Contains("zero-size items");
    }

    [Test]
    public async Task NullInANonNullField_NamesTheField()
    {
        var order = CreateOrder();
        order.CustomerName = null!;

        var ex = Assert.Throws<AvroException>(() => order.ToAvroBytes());

        await Assert.That(ex.Message).Contains("'shop.Order.customer_name' is null");
    }

    [Test]
    public async Task UnionValueOfAnotherType_IsRejected()
    {
        var order = CreateOrder();
        order.Extra = 3.5;

        var ex = Assert.Throws<AvroException>(() => order.ToAvroBytes());

        await Assert.That(ex.Message).Contains("System.Double matches no branch");
    }

    [Test]
    public async Task FixedValues_HaveExactlyTheirSize()
    {
        Assert.Throws<ArgumentException>(() => new shop.Sku(new byte[3]));
        await Assert.That(new shop.Sku([1, 2, 3, 4])).IsEqualTo(new shop.Sku([1, 2, 3, 4]));
        await Assert.That(new shop.Sku([1, 2, 3, 4])).IsNotEqualTo(new shop.Sku([1, 2, 3, 5]));
    }

    [Test]
    public async Task SchemaProperty_IsTheParsedSchemaFile()
    {
        var fromFile = AvroSchema.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schemas", "node.avsc")));

        await Assert.That(graph.Node.Schema.CanonicalForm).IsEqualTo(fromFile.CanonicalForm);
        await Assert.That(((RecordSchema)crm.Customer.Schema).GetField("last_status").Schema).IsTypeOf<EnumSchema>();
    }

    private static shop.Order CreateOrder()
    {
        var sku = new shop.Sku([0xDE, 0xAD, 0xBE, 0xEF]);
        return new shop.Order
        {
            Id = 1_790_000_000_123L,
            Active = true,
            Count = -5,
            Ratio = 0.25f,
            Total = 1234.5,
            Payload = [0, 1, 255],
            CustomerName = "Ada",
            Status = shop.Status.@class,
            Sku = sku,
            Counters = [.. Enumerable.Range(0, 64).Select(i => (long)(i * i * 1000))],
            Small = [1, -1, 1000, int.MinValue],
            Weights = [1.5, -2.25, double.MaxValue],
            Ratios = [0.5f, float.Epsilon],
            Tags = ["a", "日本", ""],
            Lines = [new shop.Line { Sku = sku, Qty = 1 }, new shop.Line { Sku = sku, Qty = 2, Note = "fragile" }],
            Attributes = new() { ["x"] = 1, ["y"] = -2 },
            Groups = new() { ["g"] = ["x", "y"], ["empty"] = [] },
            Note = "leave at the door",
            Discount = null,
            Priority = 7,
            Shipping = new shop.Line { Sku = sku, Qty = 3 },
            Extra = new List<long> { 1, 2, 3 },
            Only = "only",
            Created = 1_790_000_000_000_000L,
            Day = 20_000,
            Ref = "7f1e2d3c-0000-4000-8000-000000000001",
            Amount = [0x01, 0x86, 0xA0],
            Order_ = 99,
            Schema_ = "s",
        };
    }

    /// <summary>A chain of <paramref name="depth"/> nodes, each linking to the next through the <c>next</c> union.</summary>
    private static byte[] DeepNodeBytes(int depth)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < depth; i++)
        {
            bytes.AddRange([0x00, 0x02]);
        }

        bytes.AddRange([0x00, 0x00, 0x00, 0x00]);
        for (var i = 0; i < depth; i++)
        {
            bytes.AddRange([0x00, 0x00]);
        }

        return [.. bytes];
    }

    private static byte[] Varint(long value)
    {
        var zigZag = (ulong)((value << 1) ^ (value >> 63));
        var bytes = new List<byte>();
        while (zigZag >= 0x80)
        {
            bytes.Add((byte)(zigZag | 0x80));
            zigZag >>= 7;
        }

        bytes.Add((byte)zigZag);
        return [.. bytes];
    }
}
