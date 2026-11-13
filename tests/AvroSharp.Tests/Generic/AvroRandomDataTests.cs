using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>AvroRandomData, the public random value generator (#177).</summary>
public class AvroRandomDataTests
{
    // Every type, every logical type, a recursive record and a union with null.
    private const string EverythingSchema = """
        {"type":"record","name":"Everything","namespace":"test","fields":[
          {"name":"nothing","type":"null"},
          {"name":"flag","type":"boolean"},
          {"name":"i","type":"int"},
          {"name":"l","type":"long"},
          {"name":"f","type":"float"},
          {"name":"d","type":"double"},
          {"name":"b","type":"bytes"},
          {"name":"s","type":"string"},
          {"name":"e","type":{"type":"enum","name":"Color","symbols":["RED","GREEN","BLUE"]}},
          {"name":"x","type":{"type":"fixed","name":"Hash","size":4}},
          {"name":"list","type":{"type":"array","items":"long"}},
          {"name":"map","type":{"type":"map","values":"string"}},
          {"name":"maybe","type":["null","string","Everything"]},
          {"name":"date","type":{"type":"int","logicalType":"date"}},
          {"name":"timeMillis","type":{"type":"int","logicalType":"time-millis"}},
          {"name":"timeMicros","type":{"type":"long","logicalType":"time-micros"}},
          {"name":"tsMillis","type":{"type":"long","logicalType":"timestamp-millis"}},
          {"name":"tsMicros","type":{"type":"long","logicalType":"timestamp-micros"}},
          {"name":"localMicros","type":{"type":"long","logicalType":"local-timestamp-micros"}},
          {"name":"uuid","type":{"type":"string","logicalType":"uuid"}},
          {"name":"amount","type":{"type":"bytes","logicalType":"decimal","precision":9,"scale":2}},
          {"name":"price","type":{"type":"fixed","name":"Price","size":8,"logicalType":"decimal","precision":18,"scale":4}},
          {"name":"big","type":{"type":"bytes","logicalType":"big-decimal"}},
          {"name":"span","type":{"type":"fixed","name":"Span","size":12,"logicalType":"duration"}}
        ]}
        """;

    [Test]
    public async Task Values_RoundTripThroughTheWriterAndReader()
    {
        var schema = AvroSchema.Parse(EverythingSchema);
        var writer = GenericDatumWriter.Create(schema);
        var reader = GenericDatumReader.Create(schema);

        foreach (var value in AvroRandomData.Generate(schema, seed: 1).Take(200))
        {
            var bytes = writer.WriteToArray(value);
            await Assert.That(reader.Read(bytes)).IsEqualTo(value);
            await Assert.That(GenericDatumJsonReader.Create(schema).Read(GenericDatumJsonWriter.Create(schema).WriteToString(value))).IsEqualTo(value);
        }
    }

    [Test]
    public async Task TheSameSeed_GivesTheSameValues()
    {
        var schema = AvroSchema.Parse(EverythingSchema);

        var first = AvroRandomData.Generate(schema, seed: 9).Take(50).ToList();
        var second = AvroRandomData.Generate(schema, seed: 9).Take(50).ToList();
        var other = AvroRandomData.Generate(schema, seed: 10).Take(50).ToList();

        await Assert.That(first).IsEquivalentTo(second, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(first.SequenceEqual(other)).IsFalse();
    }

    [Test]
    public async Task TheSameSeed_GivesTheSameValues_OnEveryTargetFramework()
    {
        // The encoding of the first values for seed 42, as every target framework generates them: Random's seeded
        // algorithm and the generator's use of it must not depend on the runtime.
        var schema = AvroSchema.Parse(EverythingSchema);
        var writer = GenericDatumWriter.Create(schema);

        var bytes = AvroRandomData.Generate(schema, seed: 42).Take(3).SelectMany(v => writer.WriteToArray(v)).ToArray();
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = string.Concat(sha256.ComputeHash(bytes).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        await Assert.That(hash).IsEqualTo(ExpectedSeed42Hash);
    }

    [Test]
    public async Task LogicalValues_AreValid_AndInDotNetsRange()
    {
        var schema = (RecordSchema)AvroSchema.Parse(EverythingSchema);
        var amount = (DecimalLogicalType)schema.GetField("amount").Schema.LogicalType!;
        var price = (DecimalLogicalType)schema.GetField("price").Schema.LogicalType!;

        foreach (var value in AvroRandomData.Generate(schema, seed: 3).Take(500))
        {
            var record = value.AsRecord();
            await Assert.That(record["date"].AsInt32()).IsBetween(-719_162, 2_932_896);
            await Assert.That(record["timeMillis"].AsInt32()).IsBetween(0, 86_399_999);
            await Assert.That(record["timeMicros"].AsInt64()).IsBetween(0L, 86_399_999_999L);
            _ = DateTimeOffset.FromUnixTimeMilliseconds(record["tsMillis"].AsInt64());
            _ = DateTimeOffset.FromUnixTimeMilliseconds(record["tsMicros"].AsInt64() / 1000);
            _ = DateTimeOffset.FromUnixTimeMilliseconds(record["localMicros"].AsInt64() / 1000);
            await Assert.That(Guid.TryParseExact(record["uuid"].AsString(), "D", out _)).IsTrue();
            await Assert.That(Digits(record["amount"].AsBytes())).IsLessThanOrEqualTo(amount.Precision);
            await Assert.That(Digits(record["price"].AsFixed().GetBytesUnsafe())).IsLessThanOrEqualTo(price.Precision);
        }
    }

    [Test]
    public async Task BigDecimal_IsJavasEncoding_UnscaledBytesThenScale()
    {
        var schema = AvroSchema.Parse("""{"type":"bytes","logicalType":"big-decimal"}""");

        foreach (var value in AvroRandomData.Generate(schema, seed: 5).Take(100))
        {
            var (length, scale, atEnd) = Decode(value.AsBytes());

            await Assert.That(length).IsGreaterThan(0);
            await Assert.That(scale).IsBetween(0, 10);
            await Assert.That(atEnd).IsTrue();
        }

        static (int Length, int Scale, bool AtEnd) Decode(byte[] bytes)
        {
            var reader = new AvroSharp.IO.AvroReader(bytes);
            var unscaled = reader.ReadBytes();
            return (unscaled.Length, reader.ReadInt(), reader.IsAtEnd);
        }
    }

    [Test]
    public async Task OutOfDotNetsRange_DatesAndTimestampsTakeAnyValue()
    {
        var schema = AvroSchema.Parse("""{"type":"int","logicalType":"date"}""");
        var options = new AvroRandomDataOptions { LogicalValuesInDotNetRange = false };

        var dates = AvroRandomData.Generate(schema, seed: 2, options).Take(200).Select(v => v.AsInt32()).ToList();

        await Assert.That(dates.Any(d => d is < -719_162 or > 2_932_896)).IsTrue();
    }

    [Test]
    public async Task RecursiveSchemas_EndAtMaxDepth()
    {
        var schema = AvroSchema.Parse("""
            {"type":"record","name":"Node","fields":[
              {"name":"children","type":{"type":"array","items":"Node"}},
              {"name":"next","type":["Node","null"]}]}
            """);
        var options = new AvroRandomDataOptions { MaxDepth = 3, MaxCollectionLength = 3 };

        foreach (var value in AvroRandomData.Generate(schema, seed: 4, options).Take(100))
        {
            await Assert.That(Depth(value)).IsLessThanOrEqualTo(options.MaxDepth + 1);
        }
    }

    [Test]
    public async Task ASchemaWithNoFiniteValue_IsRejected()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Loop","fields":[{"name":"self","type":"Loop"}]}""");

        var ex = Assert.Throws<AvroException>(() => AvroRandomData.Generate(schema, seed: 1));
        Assert.Throws<AvroException>(() => new AvroRandomData(1).Next(schema));

        await Assert.That(ex.Message).Contains("no finite value");
    }

    [Test]
    public async Task UnionBranchesWithNoFiniteValue_AreNeverTaken()
    {
        // The Loop branch can't end, so every value is null, and arrays of Loop are empty.
        var schema = AvroSchema.Parse("""
            {"type":"record","name":"Holder","fields":[
              {"name":"loop","type":["null",{"type":"record","name":"Loop","fields":[{"name":"self","type":"Loop"}]}]},
              {"name":"loops","type":{"type":"array","items":"Loop"}}]}
            """);

        foreach (var value in AvroRandomData.Generate(schema, seed: 6).Take(50))
        {
            await Assert.That(value.AsRecord()["loop"].IsNull).IsTrue();
            await Assert.That(value.AsRecord()["loops"].AsArray().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Options_ShapeTheValues()
    {
        var schema = (RecordSchema)AvroSchema.Parse(EverythingSchema);
        var empty = new AvroRandomDataOptions { MaxCollectionLength = 0, MaxStringLength = 0, NullBranchProbability = 1 };
        var never = new AvroRandomDataOptions { NullBranchProbability = 0 };

        foreach (var value in AvroRandomData.Generate(schema, seed: 7, empty).Take(50))
        {
            var record = value.AsRecord();
            await Assert.That(record["list"].AsArray().Count).IsEqualTo(0);
            await Assert.That(record["map"].AsMap().Count).IsEqualTo(0);
            await Assert.That(record["s"].AsString()).IsEqualTo(string.Empty);
            await Assert.That(record["b"].AsBytes().Length).IsEqualTo(0);
            await Assert.That(record["maybe"].IsNull).IsTrue();
        }

        var nulls = AvroRandomData.Generate(schema, seed: 7, never).Take(50).Count(v => v.AsRecord()["maybe"].IsNull);
        await Assert.That(nulls).IsEqualTo(0);
    }

    [Test]
    public async Task Options_RejectOutOfRangeSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AvroRandomDataOptions { MaxCollectionLength = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AvroRandomDataOptions { MaxStringLength = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AvroRandomDataOptions { MaxDepth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AvroRandomDataOptions { NullBranchProbability = 1.5 });
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AvroRandomDataOptions { NullBranchProbability = double.NaN });

        await Assert.That(ex.ParamName).IsEqualTo("value");
    }

    private const string ExpectedSeed42Hash = "24BC1D1B7F31560C8B0BCAB2DEFBF8396B1AC9504D3AA7F559AC810DB29B05C4";

    // The number of digits of a two's-complement big-endian unscaled decimal.
    private static int Digits(byte[] bytes)
    {
        var reversed = (byte[])bytes.Clone();
        Array.Reverse(reversed);
        return BigInteger.Abs(new BigInteger(reversed)).ToString(CultureInfo.InvariantCulture).Length;
    }

    // Records nested in the value, counting the value itself.
    private static int Depth(AvroValue value) => value.Kind switch
    {
        AvroValueKind.Record => 1 + value.AsRecord().Values.Select(Depth).DefaultIfEmpty(0).Max(),
        AvroValueKind.Array => value.AsArray().Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
