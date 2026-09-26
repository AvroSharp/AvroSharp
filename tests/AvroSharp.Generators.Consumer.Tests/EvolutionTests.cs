using System;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Generated types reading data written with another version of their schema (#30, M3 part 3).</summary>
public class EvolutionTests
{
    // Version 1 of evo.Person: an int id, the old field name, a field version 2 removed, and an enum symbol version 2 lacks.
    private const string Version1 = """
        {"type":"record","name":"Person","namespace":"evo","fields":[
          {"name":"id","type":"int"},
          {"name":"nickname","type":"string"},
          {"name":"legacy","type":{"type":"array","items":"string"}},
          {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["BASIC","GOLD","PLATINUM"]}}
        ]}
        """;

    [Test]
    [Arguments("GOLD", "GOLD")]
    [Arguments("PLATINUM", "UNKNOWN")]      // not in version 2: the enum's default
    public async Task OlderData_IsResolvedToTheGeneratedType(string tier, string expected)
    {
        var writer = (RecordSchema)AvroSchema.Parse(Version1);
        var bytes = GenericDatumWriter.Create(writer).WriteToArray(new GenericRecord(writer)
        {
            ["id"] = 7,
            ["nickname"] = "Ada",
            ["legacy"] = AvroValue.FromArray(new AvroValue[] { "x", "y" }),
            ["tier"] = AvroValue.FromEnum((EnumSchema)writer.GetField("tier").Schema, tier),
        });

        var person = evo.Person.FromAvroBytes(bytes, writer);

        await Assert.That(person.Id).IsEqualTo(7L);           // int promoted to long
        await Assert.That(person.Name).IsEqualTo("Ada");      // matched through the alias
        await Assert.That(person.Email).IsNull();             // added, with its default
        await Assert.That(person.Score).IsEqualTo(1.5);
        await Assert.That(person.Tier.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task DataOfTheSameSchema_IsReadDirectly_EvenFromAnotherSchemaInstance()
    {
        var person = new evo.Person { Id = 1, Name = "Bo", Email = "bo@example.com", Score = 2, Tier = evo.Tier.GOLD };
        var bytes = person.ToAvroBytes();

        var sameInstance = evo.Person.FromAvroBytes(bytes, evo.Person.Schema);
        var parsedCopy = evo.Person.FromAvroBytes(bytes, AvroSchema.Parse(evo.Person.SchemaJson));

        await Assert.That(sameInstance.ToAvroBytes().AsSpan().SequenceEqual(bytes)).IsTrue();
        await Assert.That(parsedCopy.ToAvroBytes().AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task AnIncompatibleWriterSchema_IsRejected()
    {
        var writer = AvroSchema.Parse("""{"type":"record","name":"Person","namespace":"evo","fields":[{"name":"id","type":"string"},{"name":"name","type":"string"}]}""");

        var ex = Assert.Throws<AvroSchemaException>(() => evo.Person.FromAvroBytes([0x02, 0x41, 0x02, 0x42], writer));

        await Assert.That(ex.Message).Contains("cannot be read as");
    }
}
