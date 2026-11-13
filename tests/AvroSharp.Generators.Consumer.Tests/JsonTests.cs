using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>The Avro JSON encoding of generated types: ToAvroJson, WriteAvroJson and FromAvroJson (#19).</summary>
public class JsonTests
{
    private const int RandomIterations = 60;

    public static System.Collections.Generic.IEnumerable<string> Types() => ["shop.Order", "graph.Node", "crm.Customer", "logical.Moments"];

    [Test]
    [MethodDataSource(nameof(Types))]
    public async Task GeneratedJson_IsTheGenericJson_AndReadsBack(string type)
    {
        var (schema, toJson, fromJson) = type switch
        {
            "shop.Order" => (shop.Order.Schema, (Func<byte[], string>)(b => shop.Order.FromAvroBytes(b).ToAvroJson()), (Func<string, byte[]>)(j => shop.Order.FromAvroJson(j).ToAvroBytes())),
            "graph.Node" => (graph.Node.Schema, b => graph.Node.FromAvroBytes(b).ToAvroJson(), j => graph.Node.FromAvroJson(j).ToAvroBytes()),
            "crm.Customer" => (crm.Customer.Schema, b => crm.Customer.FromAvroBytes(b).ToAvroJson(), j => crm.Customer.FromAvroJson(j).ToAvroBytes()),
            _ => (logical.Moments.Schema, (Func<byte[], string>)(b => logical.Moments.FromAvroBytes(b).ToAvroJson()), (Func<string, byte[]>)(j => logical.Moments.FromAvroJson(j).ToAvroBytes())),
        };

        var checkedSamples = 0;
        for (var seed = 0; seed < RandomIterations; seed++)
        {
            if (new RandomValues(seed).TryCreate(schema) is not { } value)
            {
                continue;
            }

            var bytes = GenericDatumWriter.Create(schema).WriteToArray(value);
            var expected = GenericDatumJsonWriter.Create(schema).WriteToString(GenericDatumReader.Create(schema).Read(bytes));
            var json = toJson(bytes);
            if (!string.Equals(json, expected, StringComparison.Ordinal) || !fromJson(json).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException($"Seed {seed}: generated JSON differs or doesn't read back.\nGeneric:   {expected}\nGenerated: {json}");
            }

            checkedSamples++;
        }

        await Assert.That(checkedSamples).IsGreaterThanOrEqualTo(20);
    }

    [Test]
    public async Task Json_ReadsFromUtf8_AndWritesIntoALargerDocument()
    {
        var order = TestData.CreateOrder();
        var json = order.ToAvroJson();

        var fromUtf8 = shop.Order.FromAvroJson(Encoding.UTF8.GetBytes(json));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("order");
            order.WriteAvroJson(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        await Assert.That(fromUtf8.ToAvroBytes()).IsEquivalentTo(order.ToAvroBytes(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(document.RootElement.GetProperty("order").GetRawText()).IsEqualTo(json);
        await Assert.That(order.ToAvroJson(indented: true)).Contains(Environment.NewLine);
    }

    [Test]
    public async Task Json_ThatDoesNotMatchTheSchema_IsAnAvroDataException()
    {
        var ex = Assert.Throws<AvroDataException>(() => crm.Customer.FromAvroJson("""{"name": 1}"""));

        await Assert.That(ex.Message).Contains("$.name");
    }
}
