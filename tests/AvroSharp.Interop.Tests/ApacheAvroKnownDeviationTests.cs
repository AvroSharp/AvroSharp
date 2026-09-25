using System;
using System.Threading.Tasks;
using CsCheck;
using ApacheNormalization = Avro.SchemaNormalization;
using ApacheSchema = Avro.Schema;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using NamedSchema = AvroSharp.Schemas.NamedSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Places where Apache.Avro 1.12.2 (C#) differs from the specification and AvroSharp follows the specification.
/// Each test pins Apache's current behaviour, so a change in a future Apache.Avro release is noticed.
/// </summary>
public class ApacheAvroKnownDeviationTests
{
    [Test]
    public async Task FixedWithLogicalType_ApacheCanonicalFormOmitsTheDefinition()
    {
        // Specification: [STRIP] removes "logicalType", but the fixed definition itself remains.
        const string json = """{"type":"fixed","name":"Money","size":8,"logicalType":"decimal","precision":18,"scale":2}""";

        await Assert.That(AvroSchema.Parse(json).CanonicalForm).IsEqualTo("""{"name":"Money","type":"fixed","size":8}""");
        await Assert.That(ApacheNormalization.ToParsingForm(ApacheSchema.Parse(json))).IsEqualTo("\"Money\"");
    }

    [Test]
    public async Task EmptyNamespace_ApacheInheritsTheEnclosingNamespaceInstead()
    {
        // Specification: "The empty string may also be used as a namespace to indicate the null namespace."
        const string json = """
            {"type":"record","name":"Outer","namespace":"ns","fields":[
              {"name":"e","type":{"type":"enum","name":"E","namespace":"","symbols":["A"]}}]}
            """;

        var ours = (RecordSchema)AvroSchema.Parse(json);
        await Assert.That(((NamedSchema)ours.Fields[0].Schema).FullName).IsEqualTo("E");
        await Assert.That(ApacheNormalization.ToParsingForm(ApacheSchema.Parse(json))).Contains("\"name\":\"ns.E\"");
    }

    [Test]
    public async Task NullNamespaceReference_ApacheDoesNotFallBack()
    {
        // A type in the null namespace referenced from inside a namespace. AvroSharp, like Java, falls back
        // to the null namespace; Apache.Avro (C#) reports an undefined name.
        const string json = """
            {"type":"record","name":"Root","fields":[
              {"name":"child","type":{"type":"record","name":"Child","namespace":"ns","fields":[{"name":"parent","type":["null","Root"]}]}}]}
            """;

        var ours = (RecordSchema)AvroSchema.Parse(json);
        var child = (RecordSchema)ours.Fields[0].Schema;
        await Assert.That(((AvroSharp.Schemas.UnionSchema)child.Fields[0].Schema).Branches[1]).IsSameReferenceAs(ours);
        await Assert.That(() => ApacheSchema.Parse(json)).Throws<Avro.SchemaParseException>();
    }

    [Test]
    public async Task ZeroSizeFixed_ApacheRejectsIt()
    {
        // Specification: "size: an integer, specifying the number of bytes per value". AvroSharp accepts 0.
        const string json = """{"type":"fixed","name":"Empty","size":0}""";

        await Assert.That(((AvroSharp.Schemas.FixedSchema)AvroSchema.Parse(json)).Size).IsEqualTo(0);
        await Assert.That(() => ApacheSchema.Parse(json)).Throws<Avro.SchemaParseException>();
    }

    [Test]
    public async Task RandomSchemas_IncludingDeviations_RoundTripThroughAvroSharp()
    {
        RandomSchemas.Json.Sample(
            json =>
            {
                var schema = AvroSchema.Parse(json);
                var reparsed = AvroSchema.Parse(schema.ToJson());
                // The canonical form itself is not re-parsed here: a null-namespace type nested in a namespace is
                // written by its bare full name, which a parser then resolves in the enclosing namespace. That is a
                // property of the specification's [FULLNAMES] rule, not of either implementation.
                if (!string.Equals(reparsed.ToJson(), schema.ToJson(), StringComparison.Ordinal)
                    || !string.Equals(reparsed.CanonicalForm, schema.CanonicalForm, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Round trip changed the schema.\nSchema: {json}");
                }
            },
            iter: 2_000);

        await Task.CompletedTask;
    }
}
