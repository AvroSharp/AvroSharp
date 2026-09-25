using System;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>Building schemas in code, without JSON.</summary>
public class SchemaConstructionTests
{
    [Test]
    public async Task ConstructedSchema_HasTheSameCanonicalFormAsParsedJson()
    {
        var status = new EnumSchema(new SchemaName("Status", "shop"), ["NEW", "PAID"], defaultSymbol: "NEW");
        var line = new RecordSchema(new SchemaName("shop.Line"), [new RecordField("qty", AvroSchema.Int)]);
        var order = RecordSchema.CreateRecursive(
            new SchemaName("Order", "shop"),
            self =>
            [
                new RecordField("status", status),
                new RecordField("lines", new ArraySchema(line)),
                new RecordField("tags", new MapSchema(AvroSchema.String)),
                new RecordField("next", new UnionSchema([AvroSchema.Null, self]), JsonDocument.Parse("null").RootElement),
                new RecordField("id", new FixedSchema(new SchemaName("shop.Id"), 16, AvroLogicalType.Uuid)),
            ]);

        var parsed = AvroSchema.Parse("""
            {"type":"record","name":"shop.Order","fields":[
              {"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW","PAID"],"default":"NEW"}},
              {"name":"lines","type":{"type":"array","items":{"type":"record","name":"Line","fields":[{"name":"qty","type":"int"}]}}},
              {"name":"tags","type":{"type":"map","values":"string"}},
              {"name":"next","type":["null","Order"],"default":null},
              {"name":"id","type":{"type":"fixed","name":"Id","size":16,"logicalType":"uuid"}}
            ]}
            """);

        await Assert.That(order.CanonicalForm).IsEqualTo(parsed.CanonicalForm);
        await Assert.That(order.ToJson()).IsEqualTo(parsed.ToJson());
        await Assert.That(order.Fingerprint64).IsEqualTo(parsed.Fingerprint64);
    }

    [Test]
    public async Task Field_CannotBelongToTwoRecords()
    {
        var field = new RecordField("a", AvroSchema.Int);
        _ = new RecordSchema(new SchemaName("A"), [field]);
        var ex = Assert.Throws<AvroSchemaException>(() => new RecordSchema(new SchemaName("B"), [field]));
        await Assert.That(ex.Message).Contains("already belongs to record 'A'");
    }

    [Test]
    public async Task Constructors_ValidateNamesAndStructure()
    {
        Assert.Throws<AvroSchemaException>(() => new RecordField("1a", AvroSchema.Int));
        Assert.Throws<AvroSchemaException>(() => new SchemaName("bad-name"));
        Assert.Throws<AvroSchemaException>(() => new SchemaName("R", "bad..ns"));
        Assert.Throws<AvroSchemaException>(() => new SchemaName("long"));
        Assert.Throws<AvroSchemaException>(() => new EnumSchema(new SchemaName("E"), ["A", "A"]));
        Assert.Throws<AvroSchemaException>(() => new EnumSchema(new SchemaName("E"), ["A"], defaultSymbol: "B"));
        Assert.Throws<AvroSchemaException>(() => new FixedSchema(new SchemaName("F"), -1));
        Assert.Throws<AvroSchemaException>(() => new UnionSchema([AvroSchema.Int, AvroSchema.Int]));
        Assert.Throws<AvroSchemaException>(() => new UnionSchema([new UnionSchema([AvroSchema.Int])]));
        Assert.Throws<AvroSchemaException>(() => new PrimitiveSchema(AvroSchemaType.Record));
        Assert.Throws<AvroSchemaException>(() => new RecordSchema(new SchemaName("R"), [new RecordField("a", AvroSchema.Int), new RecordField("a", AvroSchema.Long)]));

        // A primitive name is fine as a namespace component, just not as a type name.
        await Assert.That(new SchemaName("R", "int.long").FullName).IsEqualTo("int.long.R");
    }

    [Test]
    [Arguments("a", true)]
    [Arguments("_a1", true)]
    [Arguments("A_B_9", true)]
    [Arguments("", false)]
    [Arguments("1a", false)]
    [Arguments("a b", false)]
    [Arguments("é", false)]
    public async Task IsValidName_FollowsTheSpecificationRule(string name, bool valid)
    {
        await Assert.That(AvroNames.IsValidName(name.AsSpan())).IsEqualTo(valid);
    }

    [Test]
    [Arguments("", true)]
    [Arguments("a", true)]
    [Arguments("a.b.c", true)]
    [Arguments("a.", false)]
    [Arguments(".a", false)]
    [Arguments("a..b", false)]
    [Arguments("a.1b", false)]
    public async Task IsValidNamespace_FollowsTheSpecificationRule(string value, bool valid)
    {
        await Assert.That(AvroNames.IsValidNamespace(value.AsSpan())).IsEqualTo(valid);
    }
}
