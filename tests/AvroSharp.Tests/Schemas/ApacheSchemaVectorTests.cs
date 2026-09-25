using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>Apache Avro's shared canonical-form and fingerprint vectors (share/test/data/schema-tests.txt).</summary>
public class ApacheSchemaVectorTests
{
    public static IEnumerable<Func<SchemaTestVector>> Vectors() =>
        TestData.ReadApacheSchemaVectors().Select(v => (Func<SchemaTestVector>)(() => v));

    [Test]
    public async Task VectorFile_IsReadCompletely()
    {
        // 34 vectors (000-033) at release-1.12.2; guards against the reader silently skipping blocks.
        var vectors = TestData.ReadApacheSchemaVectors();
        await Assert.That(vectors.Count).IsEqualTo(34);
        await Assert.That(vectors.Count(v => v.Fingerprint.HasValue)).IsEqualTo(26);
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task CanonicalForm_MatchesVector(SchemaTestVector vector)
    {
        var schema = AvroSchema.Parse(vector.Input);
        await Assert.That(schema.CanonicalForm).IsEqualTo(vector.Canonical);
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task Fingerprint_MatchesVector(SchemaTestVector vector)
    {
        var schema = AvroSchema.Parse(vector.Input);
        var expected = vector.Fingerprint ?? AvroSchema.Parse(vector.Canonical).Fingerprint64;
        await Assert.That(schema.Fingerprint64).IsEqualTo(expected);
        await Assert.That(SchemaFingerprint.Crc64Avro(System.Text.Encoding.UTF8.GetBytes(vector.Canonical))).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task CanonicalForm_IsAFixedPoint(SchemaTestVector vector)
    {
        var canonical = AvroSchema.Parse(vector.Input).CanonicalForm;
        await Assert.That(AvroSchema.Parse(canonical).CanonicalForm).IsEqualTo(canonical);
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task FullJson_RoundTrips(SchemaTestVector vector)
    {
        var schema = AvroSchema.Parse(vector.Input);
        var json = schema.ToJson();
        var reparsed = AvroSchema.Parse(json);
        await Assert.That(reparsed.ToJson()).IsEqualTo(json);
        await Assert.That(reparsed.CanonicalForm).IsEqualTo(vector.Canonical);
    }
}
