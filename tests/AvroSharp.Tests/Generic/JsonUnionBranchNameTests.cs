using System;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>GenericDatumWriterOptions.JsonUnionBranchNames: union branches labelled by simple name (#178).</summary>
public class JsonUnionBranchNameTests
{
    private const string PaymentSchema = """
        {"type":"record","name":"Payment","namespace":"com.acme","fields":[
          {"name":"method","type":["null","string",
            {"type":"record","name":"Card","fields":[{"name":"last4","type":"string"}]},
            {"type":"enum","name":"Wallet","namespace":"com.acme.wallets","symbols":["APPLE","GOOGLE"]}]}
        ]}
        """;

    private static readonly GenericDatumWriterOptions Simple = new() { JsonUnionBranchNames = JsonUnionBranchNames.Simple };

    [Test]
    public async Task ByDefault_NamedBranches_AreLabelledByFullName()
    {
        var (schema, payment) = CardPayment();

        var json = GenericDatumJsonWriter.Create(schema).WriteToString(payment);

        await Assert.That(json).IsEqualTo("""{"method":{"com.acme.Card":{"last4":"4242"}}}""");
    }

    [Test]
    public async Task Simple_LabelsNamedBranchesBySimpleName_AndReadsBack()
    {
        var (schema, payment) = CardPayment();
        var wallet = new GenericRecord(schema)
        {
            ["method"] = AvroValue.FromEnum((EnumSchema)((UnionSchema)schema.GetField("method").Schema).Branches[3], "GOOGLE"),
        };
        var writer = GenericDatumJsonWriter.Create(schema, Simple);

        var cardJson = writer.WriteToString(payment);
        var walletJson = writer.WriteToString(wallet);

        await Assert.That(cardJson).IsEqualTo("""{"method":{"Card":{"last4":"4242"}}}""");
        await Assert.That(walletJson).IsEqualTo("""{"method":{"Wallet":"GOOGLE"}}""");
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(cardJson)).IsEqualTo((AvroValue)payment);
        await Assert.That(GenericDatumJsonReader.Create(schema).Read(walletJson)).IsEqualTo((AvroValue)wallet);
    }

    [Test]
    public async Task Simple_LeavesUnnamedBranchesAsTheyAre()
    {
        var (schema, _) = CardPayment();
        var text = new GenericRecord(schema) { ["method"] = "cash" };
        var none = new GenericRecord(schema) { ["method"] = AvroValue.Null };
        var writer = GenericDatumJsonWriter.Create(schema, Simple);

        await Assert.That(writer.WriteToString(text)).IsEqualTo("""{"method":{"string":"cash"}}""");
        await Assert.That(writer.WriteToString(none)).IsEqualTo("""{"method":null}""");
    }

    [Test]
    public async Task Simple_KeepsTheFullName_OfBranchesThatShareASimpleName()
    {
        // a.Card and b.Card share "Card"; c.Cash has its simple name to itself.
        var union = (UnionSchema)AvroSchema.Parse("""
            [{"type":"record","name":"a.Card","fields":[]},
             {"type":"record","name":"b.Card","fields":[]},
             {"type":"record","name":"c.Cash","fields":[]}]
            """);
        var writer = GenericDatumJsonWriter.Create(union, Simple);
        var reader = GenericDatumJsonReader.Create(union);

        foreach (var (branch, expected) in new[] { (0, """{"a.Card":{}}"""), (1, """{"b.Card":{}}"""), (2, """{"Cash":{}}""") })
        {
            AvroValue value = new GenericRecord((RecordSchema)union.Branches[branch]);
            var json = writer.WriteToString(value);

            await Assert.That(json).IsEqualTo(expected);
            await Assert.That(reader.Read(json).AsRecord().Schema).IsSameReferenceAs(union.Branches[branch]);
        }
    }

    [Test]
    public async Task Simple_KeepsTheFullName_WhenAnotherBranchIsLabelledByTheSimpleName()
    {
        // The record without a namespace is labelled "Card" anyway, so x.Card can't be.
        var union = (UnionSchema)AvroSchema.Parse("""
            [{"type":"record","name":"x.Card","fields":[]},
             {"type":"record","name":"Card","namespace":"","fields":[]}]
            """);
        var writer = GenericDatumJsonWriter.Create(union, Simple);
        var reader = GenericDatumJsonReader.Create(union);

        var qualified = writer.WriteToString(new GenericRecord((RecordSchema)union.Branches[0]));
        var unqualified = writer.WriteToString(new GenericRecord((RecordSchema)union.Branches[1]));

        await Assert.That(qualified).IsEqualTo("""{"x.Card":{}}""");
        await Assert.That(unqualified).IsEqualTo("""{"Card":{}}""");
        await Assert.That(reader.Read(qualified).AsRecord().Schema).IsSameReferenceAs(union.Branches[0]);
        await Assert.That(reader.Read(unqualified).AsRecord().Schema).IsSameReferenceAs(union.Branches[1]);
    }

    [Test]
    public async Task UndefinedSetting_IsRejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new GenericDatumWriterOptions { JsonUnionBranchNames = (JsonUnionBranchNames)2 });

        await Assert.That(ex.ParamName).IsEqualTo("value");
    }

    private static (RecordSchema Schema, GenericRecord Payment) CardPayment()
    {
        var schema = (RecordSchema)AvroSchema.Parse(PaymentSchema);
        var card = (RecordSchema)((UnionSchema)schema.GetField("method").Schema).Branches[2];
        return (schema, new GenericRecord(schema) { ["method"] = new GenericRecord(card) { ["last4"] = "4242" } });
    }
}
