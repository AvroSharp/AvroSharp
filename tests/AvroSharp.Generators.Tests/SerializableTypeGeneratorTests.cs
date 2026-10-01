using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;

namespace AvroSharp.Generators.Tests;

/// <summary>The attribute-driven generator (#31): schemas from C# types, and their serializers.</summary>
public class SerializableTypeGeneratorTests
{
    private const string Order = """
        using System;
        using System.Collections.Generic;
        using AvroSharp.Serialization;

        namespace Shop;

        public enum Status { New, Paid, Shipped }

        [AvroSerializable]
        public partial class Line
        {
            public string Sku { get; set; } = "";
            public int Quantity { get; set; }
        }

        /// <summary>An order.</summary>
        [AvroSerializable(FieldNames = AvroNaming.CamelCase)]
        public partial class Order
        {
            public long Id { get; set; }
            public string Customer { get; set; } = "";
            public string? Note { get; set; }
            public int? Priority { get; set; }
            public List<Line> Lines { get; set; } = new();
            public Dictionary<string, string> Tags { get; set; } = new();
            public Status Status { get; set; }
            public DateTimeOffset PlacedAt { get; set; }
            public Guid Token { get; set; }
            public byte[] Payload { get; set; } = Array.Empty<byte>();
            [AvroFixed(4, Name = "Crc")] public byte[] Checksum { get; set; } = new byte[4];
            [AvroDecimal(18, 2)] public decimal Total { get; set; }
            [AvroName("legacy_ref"), AvroAlias("ref")] public string? Reference { get; set; }
            [AvroIgnore] public decimal CachedTax { get; set; }
        }

        public static class RoundTrip
        {
            public static Order Sample() => new Order
            {
                Id = 42, Customer = "Ada", Note = null, Priority = 3,
                Lines = new() { new Line { Sku = "A-1", Quantity = 2 } },
                Tags = new() { ["gift"] = "yes" },
                Status = Status.Paid, PlacedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
                Token = new Guid("6f1c1d2e-0a3b-4c5d-8e9f-001122334455"), Payload = new byte[] { 1, 2 },
                Checksum = new byte[] { 9, 8, 7, 6 }, Total = 12.34m, Reference = "R-7", CachedTax = 1m,
            };

            public static string Json(Order order) => order.Customer + "|" + order.Lines[0].Sku + "|" + order.Tags["gift"] + "|" + order.Status + "|" + order.PlacedAt.ToUnixTimeMilliseconds() + "|" + order.Token + "|" + string.Join(",", order.Payload) + "|" + string.Join(",", order.Checksum) + "|" + order.Total + "|" + order.Reference + "|" + order.Priority + "|" + (order.Note ?? "null") + "|" + order.CachedTax;

            public static string Run()
            {
                var bytes = Sample().ToAvroBytes();
                return Json(Order.FromAvroBytes(bytes));
            }
        }
        """;

    [Test]
    public async Task ATypeWithEveryMapping_GeneratesWithoutDiagnostics_AndCompilesCleanly()
    {
        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.RunTypes(Order);

        await Assert.That(generatorDiagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Length).IsEqualTo(2);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task TheSchema_FollowsTheMapping()
    {
        var assembly = GeneratorHarness.LoadTypes(Order);
        var schema = (RecordSchema)assembly.GetType("Shop.Order")!.GetProperty("Schema")!.GetValue(null)!;

        await Assert.That(schema.FullName).IsEqualTo("Shop.Order");
        await Assert.That(schema.Doc).IsEqualTo("An order.");
        await Assert.That(string.Join(",", schema.Fields.Select(f => f.Name))).IsEqualTo("id,customer,note,priority,lines,tags,status,placedAt,token,payload,checksum,total,legacy_ref");
        await Assert.That(schema.GetField("note").Schema.CanonicalForm).IsEqualTo("""["null","string"]""");
        await Assert.That(schema.GetField("note").DefaultValue!.Value.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That(schema.GetField("lines").Schema.CanonicalForm).IsEqualTo("""{"type":"array","items":{"name":"Shop.Line","type":"record","fields":[{"name":"Sku","type":"string"},{"name":"Quantity","type":"int"}]}}""");
        await Assert.That(schema.GetField("status").Schema.CanonicalForm).IsEqualTo("""{"name":"Shop.Status","type":"enum","symbols":["New","Paid","Shipped"]}""");
        await Assert.That(schema.GetField("placedAt").Schema.LogicalType!.Name).IsEqualTo("timestamp-micros");
        await Assert.That(schema.GetField("token").Schema.LogicalType!.Name).IsEqualTo("uuid");
        await Assert.That(schema.GetField("checksum").Schema.CanonicalForm).IsEqualTo("""{"name":"Shop.Crc","type":"fixed","size":4}""");
        await Assert.That(((DecimalLogicalType)schema.GetField("total").Schema.LogicalType!).Scale).IsEqualTo(2);
        await Assert.That(schema.GetField("legacy_ref").Aliases.Single()).IsEqualTo("ref");
    }

    [Test]
    public async Task Values_RoundTrip()
    {
        var assembly = GeneratorHarness.LoadTypes(Order);

        var result = (string)assembly.GetType("Shop.RoundTrip")!.GetMethod("Run")!.Invoke(null, null)!;

        await Assert.That(result).IsEqualTo("Ada|A-1|yes|Paid|1790856000000|6f1c1d2e-0a3b-4c5d-8e9f-001122334455|1,2|9,8,7,6|12.34|R-7|3|null|0");
    }
}
