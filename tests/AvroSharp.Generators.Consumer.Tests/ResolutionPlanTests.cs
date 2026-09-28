using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;
using AvroSharp.IO;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// Generated readers resolving through their plan (#69): data of older writer versions of a generated type's schema,
/// derived at random, must read to exactly what the generic resolving reader (itself checked against Apache.Avro)
/// gives, written again with the type's schema.
/// </summary>
public class ResolutionPlanTests
{
    private const int Variants = 150;

    [Test]
    public async Task OrderWriterVersions_ReadLikeTheGenericResolvingReader()
    {
        var checkedCount = Check(shop.Order.SchemaJson, shop.Order.Schema, (ref r, w) => shop.Order.Read(ref r, w).ToAvroBytes());

        await Assert.That(checkedCount).IsGreaterThan(Variants / 2);
    }

    [Test]
    public async Task PersonWriterVersions_WithDroppedFieldsAndDefaults_ReadLikeTheGenericResolvingReader()
    {
        var checkedCount = Check(evo.Person.SchemaJson, evo.Person.Schema, (ref r, w) => evo.Person.Read(ref r, w).ToAvroBytes());

        await Assert.That(checkedCount).IsGreaterThan(Variants / 2);
    }

    [Test]
    public async Task ARecordOfAnotherName_IsNotPlanned()
    {
        var other = AvroSchema.Parse("""{"type":"record","name":"Other","fields":[{"name":"id","type":"long"}]}""");

        await Assert.That(AvroGeneratedCode.GetRecordPlan(other, evo.Person.Schema)).IsNull();
        await Assert.That(AvroGeneratedCode.GetRecordPlan(AvroSchema.Parse("\"long\""), evo.Person.Schema)).IsNull();
        await Assert.That(AvroGeneratedCode.GetRecordPlan(evo.Person.Schema, evo.Person.Schema)).IsSameReferenceAs(AvroGeneratedCode.GetRecordPlan(evo.Person.Schema, evo.Person.Schema));
    }

    private delegate byte[] GeneratedRead(ref AvroReader reader, AvroSchema writerSchema);

    private static int Check(string readerJson, AvroSchema readerSchema, GeneratedRead read)
    {
        var checkedCount = 0;
        for (var seed = 0; seed < Variants; seed++)
        {
            var writerJson = WriterVersion(readerJson, new Random(seed));
            var writer = AvroSchema.Parse(writerJson);
            if (new RandomValues(seed).TryCreate(writer) is not { } value)
            {
                continue;
            }

            checkedCount++;
            var bytes = GenericDatumWriter.Create(writer).WriteToArray(value);
            var expected = GenericDatumWriter.Create(readerSchema).WriteToArray(GenericDatumReader.Create(writer, readerSchema).Read(bytes));
            var reader = new AvroReader(bytes);
            var actual = read(ref reader, writer);
            if (!actual.AsSpan().SequenceEqual(expected) || !reader.IsAtEnd)
            {
                throw new InvalidOperationException(
                    $"Seed {seed}: the generated reader disagrees.\nWriter: {writerJson}\nGenerated: {Convert.ToHexString(actual)}\nGeneric:   {Convert.ToHexString(expected)}");
            }
        }

        return checkedCount;
    }

    /// <summary>
    /// An older writer version that the reader schema can read: fields with defaults dropped, numbers demoted (also
    /// in arrays, and under logical types), enum symbols reordered, writer-only fields added, and the order shuffled.
    /// </summary>
    private static string WriterVersion(string readerJson, Random random)
    {
        var root = JsonNode.Parse(readerJson)!.AsObject();
        var fields = root["fields"]!.AsArray().Select(f => f!.DeepClone()).ToList();
        fields.RemoveAll(f => f!["default"] is not null && random.Next(2) == 0);
        foreach (var field in fields)
        {
            field!["type"] = Demote(field["type"]!, random);
            field.AsObject().Remove("default");
        }

        for (var i = random.Next(3); i > 0; i--)
        {
            fields.Insert(random.Next(fields.Count + 1), JsonNode.Parse($$"""{"name":"writer_only_{{i}}","type":{{(random.Next(2) == 0 ? "\"string\"" : """{"type":"array","items":"long"}""")}}}""")!);
        }

        // Reorder the fields that neither define nor use named types, so every name is still defined before use.
        var movable = Enumerable.Range(0, fields.Count).Where(i => IsPlain(fields[i]!["type"]!)).ToList();
        var shuffled = movable.Select(i => fields[i]).OrderBy(_ => random.Next()).ToList();
        for (var k = 0; k < movable.Count; k++)
        {
            fields[movable[k]] = shuffled[k];
        }

        root["fields"] = new JsonArray([.. fields]);
        return root.ToJsonString();
    }

    private static JsonNode Demote(JsonNode type, Random random)
    {
        switch (type)
        {
            case JsonValue value when value.TryGetValue<string>(out var name):
                return JsonValue.Create(DemotePrimitive(name, random))!;
            case JsonObject obj when obj["type"]?.GetValue<string>() is "array":
                obj["items"] = Demote(obj["items"]!.DeepClone(), random);
                return obj;
            case JsonObject obj when obj["type"]?.GetValue<string>() is "enum" && random.Next(2) == 0:
                obj["symbols"] = new JsonArray([.. obj["symbols"]!.AsArray().Reverse().Select(s => s!.DeepClone())]);
                return obj;
            case JsonObject obj when obj["logicalType"] is not null && obj["type"]?.GetValue<string>() is "long" && random.Next(2) == 0:
                return JsonValue.Create("int")!;
            default:
                return type;
        }
    }

    private static bool IsPlain(JsonNode type) => type switch
    {
        JsonValue value => value.TryGetValue<string>(out var name) && name is "null" or "boolean" or "int" or "long" or "float" or "double" or "bytes" or "string",
        JsonObject obj => obj["type"]?.GetValue<string>() is "array" && IsPlain(obj["items"]!),
        _ => false,
    };

    private static string DemotePrimitive(string name, Random random) => (name, random.Next(3)) switch
    {
        ("long", 0) => "int",
        ("double", 0) => "float",
        ("double", 1) => "long",
        ("float", 0) => "int",
        _ => name,
    };
}
