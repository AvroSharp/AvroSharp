using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Field access by position on generated records (IAvroSpecificRecord, #10).</summary>
public class GetPutTests
{
    [Test]
    public async Task CopyingEveryFieldWithGetAndPut_ProducesTheSameRecord()
    {
        var order = TestData.CreateOrder();
        var copy = new shop.Order();
        var fields = ((IAvroSpecificRecord)order).Schema.Fields.Count;

        for (var i = 0; i < fields; i++)
        {
            copy.Put(i, order.Get(i));
        }

        await Assert.That(copy.ToAvroBytes().AsSpan().SequenceEqual(order.ToAvroBytes())).IsTrue();
    }

    [Test]
    public async Task Get_ReturnsTheFieldValues_InSchemaOrder()
    {
        var order = TestData.CreateOrder();
        var schema = (RecordSchema)shop.Order.Schema;

        await Assert.That(order.Get(schema.GetField("id").Position)).IsEqualTo((object)order.Id);
        await Assert.That(order.Get(schema.GetField("customer_name").Position)).IsEqualTo((object)"Ada");
        await Assert.That(order.Get(schema.GetField("status").Position)).IsEqualTo((object)shop.Status.@class);
        await Assert.That(order.Get(schema.GetField("discount").Position)).IsNull();
        await Assert.That(order.Get(schema.GetField("priority").Position)).IsEqualTo((object)7);
        await Assert.That(ReferenceEquals(order.Get(schema.GetField("counters").Position), order.Counters)).IsTrue();
    }

    [Test]
    public async Task Put_AcceptsNullOnlyForNullableFields()
    {
        var order = TestData.CreateOrder();
        var schema = (RecordSchema)shop.Order.Schema;

        order.Put(schema.GetField("note").Position, null);
        order.Put(schema.GetField("priority").Position, null);
        await Assert.That(order.Note).IsNull();
        await Assert.That(order.Priority).IsNull();

        var ex = Assert.Throws<AvroException>(() => order.Put(schema.GetField("customer_name").Position, null));
        await Assert.That(ex.Message).Contains("'shop.Order.customer_name' holds string; a null cannot be put into it");
    }

    [Test]
    [Arguments("count", "text", "System.String")]
    [Arguments("id", 5, "System.Int32")]        // no widening, as in Apache's generated casts
    [Arguments("status", 1, "System.Int32")]    // an ordinal is not an enum value
    [Arguments("priority", 7L, "System.Int64")]
    public async Task Put_RejectsValuesOfAnotherType(string field, object value, string typeName)
    {
        var order = TestData.CreateOrder();
        var position = ((RecordSchema)shop.Order.Schema).GetField(field).Position;

        var ex = Assert.Throws<AvroException>(() => order.Put(position, value));

        await Assert.That(ex.Message).Contains($"'shop.Order.{field}'");
        await Assert.That(ex.Message).Contains(typeName);
    }

    [Test]
    public async Task UnionsMappedToObject_AcceptAnyValue_AndAreCheckedWhenWriting()
    {
        var order = TestData.CreateOrder();
        var extra = ((RecordSchema)shop.Order.Schema).GetField("extra").Position;

        order.Put(extra, "text");
        await Assert.That(order.Extra).IsEqualTo((object)"text");

        order.Put(extra, 3.5);
        Assert.Throws<AvroException>(() => order.ToAvroBytes());
    }

    [Test]
    [Arguments(-1)]
    [Arguments(30)]
    public async Task PositionsOutsideTheRecord_AreRejected(int position)
    {
        var order = TestData.CreateOrder();

        var getError = Assert.Throws<AvroException>(() => order.Get(position));
        Assert.Throws<AvroException>(() => order.Put(position, 1));

        await Assert.That(getError.Message).Contains($"no field at position {position} (30 fields)");
    }

    [Test]
    public async Task InterfaceSchema_IsTheGeneratedSchema()
    {
        IAvroSpecificRecord record = new graph.Node();

        await Assert.That(ReferenceEquals(record.Schema, graph.Node.Schema)).IsTrue();
        await Assert.That(record.Schema.FullName).IsEqualTo("graph.Node");
    }

    [Test]
    public async Task EveryGeneratedRecord_ImplementsTheInterface()
    {
        var records = new List<object> { new shop.Order(), new shop.Line(), new graph.Node(), new graph.Empty(), new crm.Customer(), new Generated.Default.Unnamespaced() };

        foreach (var record in records)
        {
            await Assert.That(record is IAvroSpecificRecord).IsTrue();
        }
    }
}
