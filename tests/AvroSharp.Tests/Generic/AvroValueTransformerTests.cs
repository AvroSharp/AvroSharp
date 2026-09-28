using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>AvroValueTransformer (#82): walking a generic value with its schema and replacing values in tagged fields.</summary>
public class AvroValueTransformerTests
{
    // Fields tagged PII, as Confluent's data contracts tag them, at every kind of nesting.
    private static readonly RecordSchema s_customer = (RecordSchema)AvroSchema.Parse("""
        {"type":"record","name":"Customer","namespace":"shop","fields":[
          {"name":"id","type":"long"},
          {"name":"name","type":"string","confluent:tags":["PII"]},
          {"name":"email","type":["null","string"],"default":null,"confluent:tags":["PII"]},
          {"name":"phones","type":{"type":"array","items":"string"},"confluent:tags":["PII"]},
          {"name":"notes","type":{"type":"map","values":"string"}},
          {"name":"address","type":["null",{"type":"record","name":"Address","fields":[
            {"name":"street","type":"string","confluent:tags":["PII"]},
            {"name":"city","type":"string"}]}],"default":null}]}
        """);

    private static readonly RecordSchema s_address = (RecordSchema)((UnionSchema)s_customer.GetField("address").Schema).Branches[1];

    [Test]
    public async Task ATransformThatChangesNothing_ReturnsTheSameInstances()
    {
        AvroValue customer = Customer();

        var result = AvroValueTransformer.Transform(s_customer, customer, static (in _, in value) => value);

        await Assert.That(ReferenceEquals(result.AsRecord(), customer.AsRecord())).IsTrue();
    }

    [Test]
    public async Task TaggedValues_AreReplaced_ThroughRecordsArraysMapsAndUnions()
    {
        AvroValue customer = Customer();
        var before = GenericDatumWriter.Create(s_customer).WriteToArray(customer);

        var result = AvroValueTransformer.Transform(s_customer, customer, UpperCasePii).AsRecord();

        await Assert.That(result["name"].AsString()).IsEqualTo("ADA LOVELACE");
        await Assert.That(result["email"].AsString()).IsEqualTo("ADA@EXAMPLE.COM");
        await Assert.That(result["phones"].AsArray().Select(p => p.AsString())).IsEquivalentTo(new[] { "+44 1", "+44 2" });
        await Assert.That(result["address"].AsRecord()["street"].AsString()).IsEqualTo("12 ST JAMES'S SQUARE");

        // Untagged values keep their instances; the input is not changed.
        await Assert.That(result["address"].AsRecord()["city"].AsString()).IsEqualTo("London");
        await Assert.That(ReferenceEquals(result["notes"].AsMap(), customer.AsRecord()["notes"].AsMap())).IsTrue();
        await Assert.That(GenericDatumWriter.Create(s_customer).WriteToArray(customer).SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task TheContext_NamesTheFieldAndTheValuesOwnSchema()
    {
        var seen = new List<(string FullName, AvroSchemaType Type, string Value)>();
        AvroValueTransformer.Transform(s_customer, Customer(), (in field, in value) =>
        {
            seen.Add((field.FullName, field.Schema.Type, value.Kind == AvroValueKind.Long ? value.AsInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : value.AsString()));
            return value;
        });

        // In schema order; the union field gets its string branch's schema, array and map values their item schemas.
        await Assert.That(seen).IsEquivalentTo(new[]
        {
            ("shop.Customer.id", AvroSchemaType.Long, "7"),
            ("shop.Customer.name", AvroSchemaType.String, "Ada Lovelace"),
            ("shop.Customer.email", AvroSchemaType.String, "ada@example.com"),
            ("shop.Customer.phones", AvroSchemaType.String, "+44 1"),
            ("shop.Customer.phones", AvroSchemaType.String, "+44 2"),
            ("shop.Customer.notes", AvroSchemaType.String, "prefers email"),
            ("shop.Address.street", AvroSchemaType.String, "12 St James's Square"),
            ("shop.Address.city", AvroSchemaType.String, "London"),
        });
    }

    [Test]
    public async Task EncryptingTaggedFields_RoundTripsThroughTheBinaryEncoding()
    {
        // A stand-in for field-level encryption: the transform is its own inverse.
        static AvroValue Scramble(in AvroFieldContext field, in AvroValue value) =>
            IsPii(field) ? new string(value.AsString().Select(c => (char)(c ^ 0x5)).ToArray()) : value;

        AvroValue customer = Customer();
        var encrypted = AvroValueTransformer.Transform(s_customer, customer, Scramble);
        var bytes = GenericDatumWriter.Create(s_customer).WriteToArray(encrypted);
        var decrypted = AvroValueTransformer.Transform(s_customer, GenericDatumReader.Create(s_customer).Read(bytes), Scramble);

        await Assert.That(encrypted.Equals(customer)).IsFalse();
        await Assert.That(decrypted.Equals(customer)).IsTrue();
    }

    [Test]
    public async Task NullsAndValuesOutsideFields_AreNotPassedToTheTransform()
    {
        var calls = 0;
        AvroValue Count(in AvroFieldContext field, in AvroValue value)
        {
            calls++;
            return value;
        }

        AvroFieldTransform count = Count;
        AvroValueTransformer.Transform(AvroSchema.Parse("\"string\""), "top level", count);
        var customer = Customer();
        customer["email"] = AvroValue.Null;
        customer["address"] = AvroValue.Null;
        AvroValueTransformer.Transform(s_customer, customer, count);

        // id, name, two phones and one note: no nulls, and nothing for the top-level string.
        await Assert.That(calls).IsEqualTo(5);
    }

    [Test]
    public async Task AValueThatMatchesNoUnionBranch_IsRejected()
    {
        var customer = Customer();
        customer["email"] = 42;

        var ex = Assert.Throws<AvroException>(() => AvroValueTransformer.Transform(s_customer, customer, UpperCasePii));

        await Assert.That(ex.Message).Contains("union");
    }

    [Test]
    public async Task ACyclicRecord_StopsAtTheDepthLimit()
    {
        var node = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"]}]}""");
        var record = new GenericRecord(node);
        record["next"] = record;

        var ex = Assert.Throws<AvroException>(() => AvroValueTransformer.Transform(node, record, UpperCasePii, maxDepth: 10));

        await Assert.That(ex.Message).Contains("10 levels");
    }

    private static AvroValue UpperCasePii(in AvroFieldContext field, in AvroValue value) =>
        IsPii(field) ? value.AsString().ToUpperInvariant() : value;

    private static bool IsPii(in AvroFieldContext field) =>
        field.Field.Properties.TryGetValue("confluent:tags", out var tags)
        && tags.ValueKind == JsonValueKind.Array
        && tags.EnumerateArray().Any(t => string.Equals(t.GetString(), "PII", StringComparison.Ordinal));

    private static GenericRecord Customer() => new(s_customer)
    {
        ["id"] = 7L,
        ["name"] = "Ada Lovelace",
        ["email"] = "ada@example.com",
        ["phones"] = AvroValue.FromArray(["+44 1", "+44 2"]),
        ["notes"] = AvroValue.FromMap(new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["contact"] = "prefers email" }),
        ["address"] = new GenericRecord(s_address) { ["street"] = "12 St James's Square", ["city"] = "London" },
    };
}
