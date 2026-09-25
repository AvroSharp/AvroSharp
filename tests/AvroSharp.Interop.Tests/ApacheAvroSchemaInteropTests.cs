using System;
using System.Linq;
using System.Threading.Tasks;
using CsCheck;
using ApacheNormalization = Avro.SchemaNormalization;
using ApacheSchema = Avro.Schema;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using SchemaFingerprint = AvroSharp.Schemas.SchemaFingerprint;

namespace AvroSharp.Interop.Tests;

/// <summary>AvroSharp and Apache.Avro must agree on canonical forms and fingerprints for any valid schema.</summary>
public class ApacheAvroSchemaInteropTests
{
    private const int Iterations = 2_000;

    [Test]
    public async Task RandomSchemas_HaveTheSameCanonicalFormAndFingerprints()
    {
        RandomSchemas.ApacheCompatibleJson.Sample(
            json => WithSchemaInErrors(json, () =>
            {
                var ours = AvroSchema.Parse(json);
                var theirs = ApacheSchema.Parse(json);

                Check(ours.CanonicalForm, ApacheNormalization.ToParsingForm(theirs), "canonical form", json);
                Check(ours.Fingerprint64, ApacheNormalization.ParsingFingerprint64(theirs), "CRC-64-AVRO", json);
                Check(Convert.ToBase64String(SchemaFingerprint.Md5(ours)), Convert.ToBase64String(ApacheNormalization.ParsingFingerprint("MD5", theirs)), "MD5", json);
                Check(Convert.ToBase64String(SchemaFingerprint.Sha256(ours)), Convert.ToBase64String(ApacheNormalization.ParsingFingerprint("SHA-256", theirs)), "SHA-256", json);
            }),
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task RandomSchemas_WrittenJsonIsReadableByTheOtherLibrary()
    {
        RandomSchemas.ApacheCompatibleJson.Sample(
            json => WithSchemaInErrors(json, () =>
            {
                var ours = AvroSchema.Parse(json);
                var theirs = ApacheSchema.Parse(json);

                // Apache.Avro reads AvroSharp's JSON back to the same schema...
                Check(ApacheNormalization.ToParsingForm(ApacheSchema.Parse(ours.ToJson())), ours.CanonicalForm, "Apache reading AvroSharp JSON", json);

                // ...and AvroSharp reads Apache.Avro's JSON back to the same schema.
                Check(AvroSchema.Parse(theirs.ToString()).CanonicalForm, ours.CanonicalForm, "AvroSharp reading Apache JSON", json);
            }),
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Generator_ProducesVariedSchemas()
    {
        // Guards against a generator that silently degenerates to trivial schemas.
        var samples = Enumerable.Range(0, 200).Select(_ => RandomSchemas.ApacheCompatibleJson.Single()).ToArray();
        await Assert.That(samples.Count(s => s.Contains("\"record\"", StringComparison.Ordinal))).IsGreaterThan(50);
        await Assert.That(samples.Count(s => s.Contains("\"namespace\"", StringComparison.Ordinal))).IsGreaterThan(20);
        await Assert.That(samples.Count(s => s.Contains("logicalType", StringComparison.Ordinal))).IsGreaterThan(20);
        await Assert.That(samples.Count(s => s.StartsWith('[') || s.Contains(":[\"", StringComparison.Ordinal))).IsGreaterThan(20);
    }

    private static void WithSchemaInErrors(string json, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"{ex.GetType().Name}: {ex.Message}\nSchema: {json}", ex);
        }
    }

    private static void Check<T>(T actual, T expected, string what, string json)
    {
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException($"{what} differs.\nSchema: {json}\nAvroSharp: {actual}\nApache:    {expected}");
        }
    }
}
