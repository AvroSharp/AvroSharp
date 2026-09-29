using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using TUnit.Assertions.Enums;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// Every logical type at edge values, written by Apache Avro Java 1.12.2 with its own conversions
/// (<c>TestData/java-avro/LogicalTypes.java</c>), so the wire form is checked against Java's and not only against this
/// library's constants or Apache.Avro C#, which deviates exactly there (#133).
/// </summary>
public class JavaLogicalTypesTests
{
    private static readonly string s_file = Path.Combine(AppContext.BaseDirectory, "TestData", "java-avro", "logical.avro");

    [Test]
    public async Task JavasLogicalValues_ReadAsTheirDotNetValues()
    {
        var rows = ReadGenerated();

        await Assert.That(rows.Select(r => r.DecimalBytes)).IsEquivalentTo(
            new[] { -12345.6789m, -0.0128m, 0.0128m, 9999999999999999.9999m, -9999999999999999.9999m }, CollectionOrdering.Matching);
        await Assert.That(rows.Select(r => r.DecimalFixed)).IsEquivalentTo(rows.Select(r => r.DecimalBytes), CollectionOrdering.Matching);

        var uuids = new[]
        {
            "00112233-4455-6677-8899-aabbccddeeff", "ffffffff-ffff-ffff-ffff-ffffffffffff", "00000000-0000-0000-0000-000000000001",
            "123e4567-e89b-12d3-a456-426614174000", "a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4",
        }.Select(Guid.Parse).ToArray();
        await Assert.That(rows.Select(r => r.UuidString)).IsEquivalentTo(uuids, CollectionOrdering.Matching);
        await Assert.That(rows.Select(r => r.UuidFixed)).IsEquivalentTo(uuids, CollectionOrdering.Matching);

        // Timestamps before 1970 and past 2038; microseconds keep six digits, milliseconds three.
        await Assert.That(rows.Select(r => r.AtMicros)).IsEquivalentTo(new[]
        {
            DateTimeOffset.Parse("1969-12-31T23:59:59.9999990Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2038-01-19T03:14:08Z", System.Globalization.CultureInfo.InvariantCulture),
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.Parse("1900-01-01T00:00:00.1234560Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2262-04-11T23:47:16.8547750Z", System.Globalization.CultureInfo.InvariantCulture),
        }, CollectionOrdering.Matching);
        await Assert.That(rows[0].AtMillis).IsEqualTo(DateTimeOffset.Parse("1969-12-31T23:59:59.999Z", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(rows[0].LocalMicros).IsEqualTo(new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).AddTicks(9_999_990));

        // timestamp-nanos keeps its long: nanoseconds from the epoch.
        await Assert.That(rows.Select(r => r.AtNanos)).IsEquivalentTo(
            new[] { -1L, 2_147_483_648_000_000_001L, 0L, -2_208_988_799_876_543_211L, long.MaxValue }, CollectionOrdering.Matching);

#if NET
        await Assert.That(rows.Select(r => r.Day)).IsEquivalentTo(
            new[] { new DateOnly(1969, 12, 31), new DateOnly(2038, 1, 19), new DateOnly(1970, 1, 1), DateOnly.MinValue, new DateOnly(9999, 12, 31) }, CollectionOrdering.Matching);
        await Assert.That(rows.Select(r => r.TimeMicros)).IsEquivalentTo(new[]
        {
            new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9_999_990)), TimeOnly.MinValue, new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(7_890_120)),
            new TimeOnly(23, 59, 59, 999), new TimeOnly(1, 2, 3, 4),
        }, CollectionOrdering.Matching);
#endif

        // duration keeps its 12 bytes: months, days and milliseconds, little-endian.
        await Assert.That(rows[0].Span.AsSpan().ToArray()).IsEquivalentTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Written again by the generated code, each row is Java's bytes: the minimal two's-complement decimals (0x80 for
    /// -128, 0x00 0x80 for +128), sign-extended fixed decimals, uuid byte order on fixed, and floored timestamps.
    /// </summary>
    [Test]
    public async Task TheGeneratedWriter_WritesJavasBytes()
    {
        var generated = ReadGenerated();

        // The generic reader keeps the underlying values, so writing them again gives the bytes as Java wrote them.
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(s_file));
        var writer = GenericDatumWriter.Create(reader.WriterSchema);
        var javas = reader.ReadAll().Select(value => writer.WriteToArray(value)).ToList();

        await Assert.That(generated.Count).IsEqualTo(5);
        for (var i = 0; i < generated.Count; i++)
        {
            await Assert.That(generated[i].ToAvroBytes()).IsEquivalentTo(javas[i], CollectionOrdering.Matching);
        }
    }

    private static System.Collections.Generic.List<java.logical.Edges> ReadGenerated()
    {
        using var reader = AvroFileReader.Open<java.logical.Edges>(File.OpenRead(s_file), _ => java.logical.Edges.Read);
        return [.. reader.ReadAll()];
    }
}
