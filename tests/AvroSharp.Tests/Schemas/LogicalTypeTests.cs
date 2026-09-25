using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>The "Logical Types" section of the specification.</summary>
public class LogicalTypeTests
{
    [Test]
    [Arguments("""{"type":"string","logicalType":"uuid"}""", AvroLogicalTypeKind.Uuid)]
    [Arguments("""{"type":"fixed","name":"U","size":16,"logicalType":"uuid"}""", AvroLogicalTypeKind.Uuid)]
    [Arguments("""{"type":"int","logicalType":"date"}""", AvroLogicalTypeKind.Date)]
    [Arguments("""{"type":"int","logicalType":"time-millis"}""", AvroLogicalTypeKind.TimeMillis)]
    [Arguments("""{"type":"long","logicalType":"time-micros"}""", AvroLogicalTypeKind.TimeMicros)]
    [Arguments("""{"type":"long","logicalType":"timestamp-millis"}""", AvroLogicalTypeKind.TimestampMillis)]
    [Arguments("""{"type":"long","logicalType":"timestamp-micros"}""", AvroLogicalTypeKind.TimestampMicros)]
    [Arguments("""{"type":"long","logicalType":"timestamp-nanos"}""", AvroLogicalTypeKind.TimestampNanos)]
    [Arguments("""{"type":"long","logicalType":"local-timestamp-millis"}""", AvroLogicalTypeKind.LocalTimestampMillis)]
    [Arguments("""{"type":"long","logicalType":"local-timestamp-micros"}""", AvroLogicalTypeKind.LocalTimestampMicros)]
    [Arguments("""{"type":"long","logicalType":"local-timestamp-nanos"}""", AvroLogicalTypeKind.LocalTimestampNanos)]
    [Arguments("""{"type":"fixed","name":"D","size":12,"logicalType":"duration"}""", AvroLogicalTypeKind.Duration)]
    [Arguments("""{"type":"bytes","logicalType":"big-decimal"}""", AvroLogicalTypeKind.BigDecimal)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}""", AvroLogicalTypeKind.Decimal)]
    [Arguments("""{"type":"fixed","name":"Dec","size":8,"logicalType":"decimal","precision":18}""", AvroLogicalTypeKind.Decimal)]
    public async Task ValidLogicalType_IsRecognisedAndItsAttributesConsumed(string json, AvroLogicalTypeKind kind)
    {
        var schema = AvroSchema.Parse(json);
        await Assert.That(schema.LogicalType).IsNotNull();
        await Assert.That(schema.LogicalType!.Kind).IsEqualTo(kind);
        await Assert.That(schema.Properties.ContainsKey("logicalType")).IsFalse();
        await Assert.That(schema.Properties.ContainsKey("precision")).IsFalse();

        // The logical type survives a JSON round trip.
        var reparsed = AvroSchema.Parse(schema.ToJson());
        await Assert.That(reparsed.LogicalType!.Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task Decimal_ReadsPrecisionAndScale()
    {
        var schema = AvroSchema.Parse("""{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}""");
        var decimalType = (DecimalLogicalType)schema.LogicalType!;
        await Assert.That(decimalType.Precision).IsEqualTo(10);
        await Assert.That(decimalType.Scale).IsEqualTo(2);

        var noScale = (DecimalLogicalType)AvroSchema.Parse("""{"type":"bytes","logicalType":"decimal","precision":5}""").LogicalType!;
        await Assert.That(noScale.Scale).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"long","logicalType":"date"}""")]
    [Arguments("""{"type":"int","logicalType":"timestamp-millis"}""")]
    [Arguments("""{"type":"string","logicalType":"duration"}""")]
    [Arguments("""{"type":"fixed","name":"U","size":15,"logicalType":"uuid"}""")]
    [Arguments("""{"type":"fixed","name":"D","size":16,"logicalType":"duration"}""")]
    [Arguments("""{"type":"string","logicalType":"big-decimal"}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal"}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":0}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":2,"scale":3}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":2,"scale":-1}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":"2"}""")]
    [Arguments("""{"type":"fixed","name":"Dec","size":8,"logicalType":"decimal","precision":19}""")]
    [Arguments("""{"type":"int","logicalType":"decimal","precision":4}""")]
    [Arguments("""{"type":"string","logicalType":"not-a-logical-type"}""")]
    [Arguments("""{"type":"string","logicalType":42}""")]
    public async Task InvalidOrUnknownLogicalType_IsIgnoredAndKeptAsProperties(string json)
    {
        var schema = AvroSchema.Parse(json);
        await Assert.That(schema.LogicalType).IsNull();
        await Assert.That(schema.Properties.ContainsKey("logicalType")).IsTrue();

        // Ignored logical types still round-trip, and do not affect the canonical form.
        await Assert.That(AvroSchema.Parse(schema.ToJson()).ToJson()).IsEqualTo(schema.ToJson());
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(2, 4)]
    [Arguments(4, 9)]
    [Arguments(8, 18)]
    [Arguments(16, 38)]
    [Arguments(32, 76)]
    public async Task MaxPrecisionForFixedSize_FollowsTheSpecificationFormula(int size, int expected)
    {
        await Assert.That(DecimalLogicalType.MaxPrecisionForFixedSize(size)).IsEqualTo(expected);
    }

    [Test]
    public async Task LogicalTypes_AreStrippedFromTheCanonicalForm()
    {
        var schema = AvroSchema.Parse("""{"type":"long","logicalType":"timestamp-micros"}""");
        await Assert.That(schema.CanonicalForm).IsEqualTo("\"long\"");
        await Assert.That(schema.Fingerprint64).IsEqualTo(AvroSchema.Long.Fingerprint64);
    }

    [Test]
    public async Task Constructors_RejectLogicalTypesThatDoNotApply()
    {
        Assert.Throws<AvroSchemaException>(() => new PrimitiveSchema(AvroSchemaType.Long, AvroLogicalType.Date));
        Assert.Throws<AvroSchemaException>(() => new FixedSchema(new SchemaName("F"), 8, AvroLogicalType.Decimal(19)));
        Assert.Throws<AvroSchemaException>(() => AvroLogicalType.Decimal(0));
        Assert.Throws<AvroSchemaException>(() => AvroLogicalType.Decimal(4, 5));

        var ok = new FixedSchema(new SchemaName("F"), 8, AvroLogicalType.Decimal(18, 4));
        await Assert.That(ok.LogicalType).IsEqualTo(AvroLogicalType.Decimal(18, 4));
    }
}
