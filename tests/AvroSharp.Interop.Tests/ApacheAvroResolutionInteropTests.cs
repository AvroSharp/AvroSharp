using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvroSharp.Generic;
using CsCheck;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Schema resolution checked against Apache.Avro's resolving reader: random schemas are evolved into valid reader
/// schemas, data written with the writer schema is resolved by both libraries, and both results, written again with
/// the reader schema, must be the same bytes.
/// </summary>
public class ApacheAvroResolutionInteropTests
{
    private const int Iterations = 600;

    [Test]
    public async Task RandomEvolutions_ResolveLikeApache()
    {
        var checkedSamples = 0;
        var evolvedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (writerJson, seed) =>
            {
                var writer = AvroSchema.Parse(writerJson);
                // Apache.Avro's resolving reader overflows the stack on recursive records (see SchemaShapes).
                if (SchemaShapes.IsRecursive(writer) || new RandomValues(seed).TryCreate(writer) is not { } value)
                {
                    return;
                }

                var evolver = new Evolver(new Random(seed));
                var readerJson = evolver.Evolve(writerJson);
                var reader = AvroSchema.Parse(readerJson);
                if (evolver.Changes > 0)
                {
                    System.Threading.Interlocked.Increment(ref evolvedSamples);
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);

                var bytes = GenericDatumWriter.Create(writer).WriteToArray(value);
                var ours = GenericDatumWriter.Create(reader).WriteToArray(GenericDatumReader.Create(writer, reader).Read(bytes));

                var apacheWriterSchema = ApacheSchema.Parse(writerJson);
                var apacheReaderSchema = ApacheSchema.Parse(readerJson);
                using var input = new MemoryStream(bytes);
                var apacheValue = new ApacheReader(apacheWriterSchema, apacheReaderSchema).Read(null!, new Avro.IO.BinaryDecoder(input));
                using var output = new MemoryStream();
                new ApacheWriter(apacheReaderSchema).Write(apacheValue, new Avro.IO.BinaryEncoder(output));

                if (!output.ToArray().AsSpan().SequenceEqual(ours))
                {
                    throw new InvalidOperationException(
                        $"Resolved values differ.\nWriter: {writerJson}\nReader: {readerJson}\nAvroSharp: {Convert.ToHexString(ours)}\nApache:    {Convert.ToHexString(output.ToArray())}");
                }

                // The transcoder that generated types use must produce the same reader encoding directly.
                var bytesReader = new AvroSharp.IO.AvroReader(bytes);
                var transcoded = AvroSharp.Serialization.Generated.AvroGeneratedCode.ResolveToReaderEncoding(ref bytesReader, writer, reader).ToArray();
                if (!transcoded.AsSpan().SequenceEqual(ours) || !bytesReader.IsAtEnd)
                {
                    throw new InvalidOperationException(
                        $"Transcoding differs.\nWriter: {writerJson}\nReader: {readerJson}\nResolved:   {Convert.ToHexString(ours)}\nTranscoded: {Convert.ToHexString(transcoded)}");
                }
            },
            iter: Iterations);

        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
        // Many random schemas have nothing to evolve (a primitive, an enum left alone); enough of them must change.
        await Assert.That(evolvedSamples).IsGreaterThan(checkedSamples / 4);
    }

    /// <summary>
    /// Derives a reader schema that the specification says can read data of the writer schema: drops fields, adds
    /// fields with defaults, reverses field order, promotes numbers, and appends enum symbols. Fields that define a
    /// named type are never dropped, and records containing such fields are never reordered, so every name is still
    /// defined before it is used.
    /// </summary>
    private sealed class Evolver(Random random)
    {
        private int _added;

        public int Changes { get; private set; }

        public string Evolve(string writerJson)
        {
            var root = JsonNode.Parse(writerJson)!;
            return (Visit(root) ?? root).ToJsonString();
        }

        /// <summary>Evolves a schema node in place; returns a replacement for a primitive name, or null.</summary>
        private JsonValue? Visit(JsonNode node)
        {
            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var name):
                    return Promote(name) is { } promoted ? JsonValue.Create(promoted) : null;
                case JsonArray union:
                    EvolveUnion(union);
                    return null;
                case JsonObject obj:
                    switch ((string?)obj["type"])
                    {
                        case "record":
                            EvolveRecord(obj);
                            break;
                        case "enum" when random.Next(3) == 0:
                            ((JsonArray)obj["symbols"]!).Add("EVOLVED");
                            Changes++;
                            break;
                        case "array":
                            Replace(obj, "items");
                            break;
                        case "map":
                            Replace(obj, "values");
                            break;
                    }

                    return null;
                default:
                    return null;
            }
        }

        private void Replace(JsonObject obj, string property)
        {
            if (Visit(obj[property]!) is { } replacement)
            {
                obj[property] = replacement;
            }
        }

        private string? Promote(string name)
        {
            if (random.Next(3) != 0)
            {
                return null;
            }

            var promoted = name switch
            {
                "int" => random.Next(2) == 0 ? "long" : "double",
                "long" => "double",
                "float" => "double",
                _ => null,
            };
            if (promoted is not null)
            {
                Changes++;
            }

            return promoted;
        }

        private void EvolveUnion(JsonArray union)
        {
            for (var i = 0; i < union.Count; i++)
            {
                var branch = union[i]!;
                if (branch is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    // A union may not hold the same primitive twice.
                    if (Promote(name) is { } promoted && !union.Any(b => b is JsonValue v && v.TryGetValue<string>(out var other) && string.Equals(other, promoted, StringComparison.Ordinal)))
                    {
                        union[i] = promoted;
                    }
                }
                else
                {
                    Visit(branch);
                }
            }
        }

        private void EvolveRecord(JsonObject record)
        {
            var fields = ((JsonArray)record["fields"]!).Select(f => f!.DeepClone().AsObject()).ToList();
            var kept = new List<JsonObject>();
            foreach (var field in fields)
            {
                if (!DefinesNamedType(field["type"]!) && random.Next(5) == 0)
                {
                    Changes++;
                    continue;
                }

                if (Visit(field["type"]!) is { } promoted)
                {
                    field["type"] = promoted;
                }

                kept.Add(field);
            }

            if (random.Next(3) == 0)
            {
                kept.Add(NewField());
                Changes++;
            }

            if (kept.Count > 1 && !fields.Any(f => DefinesNamedType(f["type"]!)) && random.Next(3) == 0)
            {
                kept.Reverse();
                Changes++;
            }

            record["fields"] = new JsonArray([.. kept]);
        }

        private JsonObject NewField()
        {
            var name = "evolved_" + _added++;
            return random.Next(3) switch
            {
                0 => new JsonObject { ["name"] = name, ["type"] = "long", ["default"] = 42 },
                1 => new JsonObject { ["name"] = name, ["type"] = new JsonArray("null", "string"), ["default"] = null },
                _ => new JsonObject { ["name"] = name, ["type"] = "string", ["default"] = "evolved" },
            };
        }

        private static bool DefinesNamedType(JsonNode node) => node switch
        {
            JsonObject obj => (string?)obj["type"] is "record" or "enum" or "fixed"
                || obj.Select(p => p.Value).OfType<JsonNode>().Any(DefinesNamedType),
            JsonArray array => array.OfType<JsonNode>().Any(DefinesNamedType),
            _ => false,
        };
    }
}
