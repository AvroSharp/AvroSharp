using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Tests.IO;
using TUnit.Assertions.Enums;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// Generated readers over input that is one byte per segment, with empty segments between (#133). Only the reader's
/// span path was tested: this is the slow path of <c>ReadFixed</c>, and <c>AvroReader.Skip</c>, the plan and the
/// transcoder across segment boundaries.
/// </summary>
public class SegmentedInputTests
{
    [Test]
    public async Task EveryFieldShape_ReadsTheSame_FromOneBytePerSegment()
    {
        var bytes = TestData.CreateOrder().ToAvroBytes();
        var segments = Segments.ByteByByte(bytes);

        await Assert.That(shop.Order.FromAvroBytes(in segments).ToAvroBytes()).IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

    [Test]
    public async Task LogicalTypesAndFixedValues_ReadTheSame_FromOneBytePerSegment()
    {
        var schema = logical.Moments.Schema;
        var bytes = logical.Moments.FromAvroBytes(GenericDatumWriter.Create(schema).WriteToArray(new RandomValues(3).TryCreate(schema)!.Value)).ToAvroBytes();
        var segments = Segments.ByteByByte(bytes);

        await Assert.That(logical.Moments.FromAvroBytes(in segments).ToAvroBytes()).IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

    /// <summary>Version 1 removes an array (skipped) and has an int the generated type reads as long (promoted).</summary>
    [Test]
    [Arguments("GOLD")]
    [Arguments("PLATINUM")]
    public async Task OlderData_ResolvesTheSame_FromOneBytePerSegment(string tier)
    {
        var writer = (RecordSchema)AvroSchema.Parse(EvolutionTests.Version1);
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(new GenericRecord(writer)
        {
            ["id"] = 7,
            ["nickname"] = "Ada",
            ["legacy"] = AvroValue.FromArray(new AvroValue[] { "x", "y", "z" }),
            ["tier"] = AvroValue.FromEnum((EnumSchema)writer.GetField("tier").Schema, tier),
        });
        var expected = evo.Person.FromAvroBytes(bytes, writer).ToAvroBytes();

        var reader = new AvroReader(Segments.ByteByByte(bytes));
        var person = evo.Person.Read(ref reader, writer);
        var atEnd = reader.IsAtEnd;

        await Assert.That(person.ToAvroBytes()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(atEnd).IsTrue();
    }

    [Test]
    public async Task AWideRecordInAnotherFieldOrder_ResolvesTheSame_FromOneBytePerSegment()
    {
        var schema = (RecordSchema)wide.Wide.Schema;
        var generic = new RandomValues(7).TryCreate(schema)!.Value;
        var fields = string.Join(",", schema.Fields.Reverse().Select(f => $$"""{"name":"{{f.Name}}","type":{{f.Schema.ToJson()}}}"""));
        var writer = (RecordSchema)AvroSchema.Parse($$"""{"type":"record","name":"Wide","namespace":"wide","fields":[{{fields}}]}""");
        var reordered = new GenericRecord(writer);
        foreach (var field in schema.Fields)
        {
            reordered[field.Name] = generic.AsRecord()[field.Name];
        }

        var bytes = GenericDatumWriter.Create(writer).WriteToArray(reordered);
        var reader = new AvroReader(Segments.ByteByByte(bytes));
        var resolved = wide.Wide.Read(ref reader, writer);

        await Assert.That(resolved.ToAvroBytes()).IsEquivalentTo(GenericDatumWriter.Create(schema).WriteToArray(generic), CollectionOrdering.Matching);
    }
}
