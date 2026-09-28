using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests;

/// <summary>The README's getting-started examples, kept compiling and correct.</summary>
public class ReadmeExamplesTests
{
    [Test]
    public async Task GettingStarted_WritesReadsResolvesAndConvertsToJson()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"User","namespace":"example","fields":[
              {"name":"id","type":"int"},
              {"name":"name","type":"string"}]}
            """);

        var user = new GenericRecord(schema) { ["id"] = 1, ["name"] = "Ada" };
        byte[] bytes = GenericDatumWriter.Create(schema).WriteToArray(user);

        GenericRecord copy = GenericDatumReader.Create(schema).Read(bytes).AsRecord();
        string name = copy["name"].AsString();

        var v2 = AvroSchema.Parse("""
            {"type":"record","name":"User","namespace":"example","fields":[
              {"name":"id","type":"long"},
              {"name":"name","type":"string"},
              {"name":"active","type":"boolean","default":true}]}
            """);

        GenericRecord upgraded = GenericDatumReader.Create(writerSchema: schema, readerSchema: v2).Read(bytes).AsRecord();
        bool active = upgraded["active"].AsBoolean();

        string json = GenericDatumJsonWriter.Create(schema).WriteToString(user);
        AvroValue fromJson = GenericDatumJsonReader.Create(schema).Read(json);

        await Assert.That(name).IsEqualTo("Ada");
        await Assert.That(upgraded["id"].AsInt64()).IsEqualTo(1L);
        await Assert.That(active).IsTrue();
        await Assert.That(json).IsEqualTo("""{"id":1,"name":"Ada"}""");
        await Assert.That(fromJson.AsRecord()["name"].AsString()).IsEqualTo("Ada");
    }
}
