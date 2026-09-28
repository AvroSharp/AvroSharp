using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Logical types in generated code (#11): .NET types in the API, the specification's encoding on the wire.</summary>
public class LogicalTypesTests
{
    private static readonly Guid s_uuid = new("00112233-4455-6677-8899-aabbccddeeff");

    [Test]
    public async Task Properties_UseTheNativeMapping()
    {
#if NET6_0_OR_GREATER
        var (date, time) = (typeof(DateOnly), typeof(TimeOnly));
#else
        var (date, time) = (typeof(DateTime), typeof(TimeSpan));
#endif
        var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Day"] = date,
            ["ClockMillis"] = time,
            ["ClockMicros"] = time,
            ["AtMillis"] = typeof(DateTimeOffset),
            ["AtMicros"] = typeof(DateTimeOffset),
            ["AtNanos"] = typeof(long),                 // no .NET type holds nanoseconds exactly
            ["LocalMillis"] = typeof(DateTime),
            ["LocalMicros"] = typeof(DateTime),
            ["Id"] = typeof(Guid),
            ["IdFixed"] = typeof(Guid),
            ["Price"] = typeof(decimal),
            ["PriceFixed"] = typeof(decimal),
            ["Huge"] = typeof(byte[]),                  // precision 30 exceeds System.Decimal
            ["Elapsed"] = typeof(logical.Elapsed),      // duration keeps its fixed type
            ["MaybeDay"] = typeof(Nullable<>).MakeGenericType(date),
            ["Days"] = typeof(List<>).MakeGenericType(date),
            ["Stamps"] = typeof(Dictionary<string, DateTimeOffset>),
        };

        foreach (var (name, type) in expected)
        {
            await Assert.That(typeof(logical.Moments).GetProperty(name)!.PropertyType).IsEqualTo(type).Because(name);
        }
    }

    [Test]
    public async Task KnownValues_AreEncodedAsTheSpecificationDefines()
    {
        var moments = CreateMoments();

        var bytes = moments.ToAvroBytes();
        var generic = GenericDatumReader.Create(logical.Moments.Schema).Read(bytes).AsRecord();

        await Assert.That(generic["day"].AsInt32()).IsEqualTo(19_998);                          // 2024-10-02
        await Assert.That(generic["clock_millis"].AsInt32()).IsEqualTo(86_399_999);             // 23:59:59.999
        await Assert.That(generic["clock_micros"].AsInt64()).IsEqualTo(45_296_789_012L);         // 12:34:56.789012
        await Assert.That(generic["at_millis"].AsInt64()).IsEqualTo(-1L);                       // one millisecond before the epoch
        await Assert.That(generic["at_micros"].AsInt64()).IsEqualTo(1_727_870_400_000_001L);
        await Assert.That(generic["local_millis"].AsInt64()).IsEqualTo(0L);
        await Assert.That(generic["id"].AsString()).IsEqualTo("00112233-4455-6677-8899-aabbccddeeff");
        await Assert.That(Convert.ToHexString(generic["id_fixed"].AsFixed().Bytes.ToArray())).IsEqualTo("00112233445566778899AABBCCDDEEFF"); // RFC 4122 order
        await Assert.That(Convert.ToHexString(generic["price"].AsBytes())).IsEqualTo("01E240");             // 12.3456 at scale 4
        await Assert.That(Convert.ToHexString(generic["price_fixed"].AsFixed().Bytes.ToArray())).IsEqualTo("FFFFFFFFFFFFFF6A"); // -1.50 at scale 2

        var back = logical.Moments.FromAvroBytes(bytes);
        await Assert.That(back.ToAvroBytes().AsSpan().SequenceEqual(bytes)).IsTrue();
        await Assert.That(back.Id).IsEqualTo(s_uuid);
        await Assert.That(back.IdFixed).IsEqualTo(s_uuid);
        await Assert.That(back.Price).IsEqualTo(12.3456m);
        await Assert.That(back.PriceFixed).IsEqualTo(-1.50m);
        await Assert.That(back.AtMillis).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(-1));
    }

    [Test]
    public async Task Timestamps_AreTruncatedTowardsNegativeInfinity()
    {
        var moments = CreateMoments();
        moments.AtMillis = DateTimeOffset.FromUnixTimeMilliseconds(0).AddTicks(-1);       // 100 ns before the epoch
        moments.AtMicros = DateTimeOffset.FromUnixTimeMilliseconds(5).AddTicks(9);        // 5 ms + 900 ns

        var back = logical.Moments.FromAvroBytes(moments.ToAvroBytes());

        await Assert.That(back.AtMillis).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(-1));
        await Assert.That(back.AtMicros).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(5));
    }

    [Test]
    [Arguments("1.23456", "more than 4 fractional digits")]
    [Arguments("123456789.1", "more than 12 digits")]
    public async Task Decimals_AreExact_NotRounded(string value, string message)
    {
        var moments = CreateMoments();
        moments.Price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var ex = Assert.Throws<AvroException>(() => moments.ToAvroBytes());

        await Assert.That(ex.Message).Contains(message);
    }

    [Test]
    [Arguments("day", "outside the range of Date")]
    [Arguments("id", "is not a UUID")]
    [Arguments("clock_millis", "not a time of day")]
    public async Task ValuesOutsideTheDotNetRange_AreRejectedWhenReading(string field, string message)
    {
        // The generic model writes the raw values that generated code cannot represent.
        var record = GenericDatumReader.Create(logical.Moments.Schema).Read(CreateMoments().ToAvroBytes()).AsRecord();
        record[field] = field switch
        {
            "day" => (AvroValue)int.MaxValue,
            "id" => "not-a-uuid",
            _ => (AvroValue)(-1),
        };
        var bytes = GenericDatumWriter.Create(logical.Moments.Schema).WriteToArray(record);

        var ex = Assert.Throws<AvroDataException>(() => logical.Moments.FromAvroBytes(bytes));

        await Assert.That(ex.Message).Contains(message);
    }

    [Test]
    public async Task ApacheAvro_ReadsTheGeneratedBytes_AndWritesThemBackIdentically()
    {
        var bytes = CreateMoments().ToAvroBytes();

        // Apache.Avro 1.12.2 rejects uuid on fixed(16), which the specification allows (see ApacheAvroKnownDeviationTests).
        // Without the logical type the fixed is the same 16 bytes on the wire.
#if NET6_0_OR_GREATER
        var apacheJson = logical.Moments.SchemaJson.Replace("\"size\":16,\"logicalType\":\"uuid\"", "\"size\":16", StringComparison.Ordinal);
#else
        var apacheJson = logical.Moments.SchemaJson.Replace("\"size\":16,\"logicalType\":\"uuid\"", "\"size\":16");
#endif
        var apacheSchema = ApacheSchema.Parse(apacheJson);
        using var input = new MemoryStream(bytes);
        var apacheValue = new ApacheReader(apacheSchema, apacheSchema).Read(null!, new Avro.IO.BinaryDecoder(input));
        using var output = new MemoryStream();
        new ApacheWriter(apacheSchema).Write(apacheValue, new Avro.IO.BinaryEncoder(output));

        await Assert.That(Convert.ToHexString(output.ToArray())).IsEqualTo(Convert.ToHexString(bytes));
    }

    private static logical.Moments CreateMoments() => new()
    {
#if NET6_0_OR_GREATER
        Day = new DateOnly(2024, 10, 2),
        ClockMillis = new TimeOnly(23, 59, 59, 999),
        ClockMicros = new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(7_890_120)),
        MaybeDay = new DateOnly(1969, 12, 31),
        Days = [new DateOnly(1, 1, 1), new DateOnly(9999, 12, 31)],
#else
        Day = new DateTime(2024, 10, 2),
        ClockMillis = new TimeSpan(0, 23, 59, 59, 999),
        ClockMicros = new TimeSpan(12, 34, 56).Add(TimeSpan.FromTicks(7_890_120)),
        MaybeDay = new DateTime(1969, 12, 31),
        Days = [new DateTime(1, 1, 1), new DateTime(9999, 12, 31)],
#endif
        AtMillis = DateTimeOffset.FromUnixTimeMilliseconds(-1),
        AtMicros = DateTimeOffset.FromUnixTimeMilliseconds(1_727_870_400_000).AddTicks(10),
        AtNanos = 1_727_870_400_000_000_123L,
        LocalMillis = new DateTime(1970, 1, 1),
        LocalMicros = new DateTime(2024, 10, 2, 12, 0, 0, DateTimeKind.Local),
        Id = s_uuid,
        IdFixed = s_uuid,
        Price = 12.3456m,
        PriceFixed = -1.50m,
        Huge = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        Elapsed = new logical.Elapsed(new byte[12]),
        Stamps = new() { ["start"] = DateTimeOffset.FromUnixTimeMilliseconds(1_727_870_400_000) },
    };
}
