using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Fuzz;

/// <summary>
/// Fuzz targets. Each one feeds arbitrary bytes to a parser and fails (throws something other than
/// <see cref="AvroException"/>) when the input exposes a bug: a crash, an unexpected exception type, or a value that
/// does not survive a round trip. Shared by the libFuzzer harness and the mutation smoke test in the test suite.
/// </summary>
public static class FuzzTargets
{
    /// <summary>Schemas the data targets choose from with the first input byte: every type, logical types and recursion.</summary>
    public static IReadOnlyList<AvroSchema> Schemas { get; } =
    [
        AvroSchema.Parse("""
            {"type":"record","name":"Everything","namespace":"fuzz","fields":[
              {"name":"n","type":"null"},{"name":"b","type":"boolean"},{"name":"i","type":"int"},{"name":"l","type":"long"},
              {"name":"f","type":"float"},{"name":"d","type":"double"},{"name":"by","type":"bytes"},{"name":"s","type":"string"},
              {"name":"e","type":{"type":"enum","name":"E","symbols":["A","B","C"]}},
              {"name":"fx","type":{"type":"fixed","name":"F","size":3}},
              {"name":"a","type":{"type":"array","items":"long"}},
              {"name":"ai","type":{"type":"array","items":"int"}},
              {"name":"ad","type":{"type":"array","items":"double"}},
              {"name":"m","type":{"type":"map","values":["null","string","F"]}},
              {"name":"u","type":["null","int","long","float","double","bytes","string","E"]},
              {"name":"ts","type":{"type":"long","logicalType":"timestamp-micros"}},
              {"name":"empty","type":{"type":"record","name":"Empty","fields":[]}},
              {"name":"nulls","type":{"type":"array","items":"null"}}
            ]}
            """),
        AvroSchema.Parse("""{"type":"record","name":"Node","fields":[{"name":"v","type":"int"},{"name":"next","type":["null","Node"]},{"name":"kids","type":{"type":"array","items":"Node"}}]}"""),
        AvroSchema.Parse("""{"type":"map","values":{"type":"array","items":["null","double","string"]}}"""),
        AvroSchema.Parse("\"string\""),
    ];

    /// <summary>Binary data: read, then write and read again, in binary and in JSON.</summary>
    public static void GenericBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var schema = Schemas[data[0] % Schemas.Count];
        AvroValue value;
        try
        {
            value = GenericDatumReader.Create(schema).Read(data[1..]);
        }
        catch (AvroException)
        {
            return;
        }

        CheckRoundTrips(schema, value);
    }

    /// <summary>JSON data: read, then write and read again, in binary and in JSON.</summary>
    public static void GenericJson(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var schema = Schemas[data[0] % Schemas.Count];
        AvroValue value;
        try
        {
            value = GenericDatumJsonReader.Create(schema).Read(data[1..]);
        }
        catch (AvroException)
        {
            return;
        }

        CheckRoundTrips(schema, value);
    }

    /// <summary>Schema JSON: parse, then write and parse again to the same canonical form.</summary>
    public static void SchemaParse(ReadOnlySpan<byte> data)
    {
        AvroSchema schema;
        try
        {
            schema = AvroSchema.Parse(data);
        }
        catch (AvroException)
        {
            return;
        }

        var again = AvroSchema.Parse(schema.ToJson());
        if (!string.Equals(again.CanonicalForm, schema.CanonicalForm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Canonical form changed after a round trip: {schema.CanonicalForm} became {again.CanonicalForm}.");
        }
    }

    /// <summary>Valid inputs to start fuzzing from, per target: encodings of sample values for every schema.</summary>
    public static IEnumerable<(string Target, byte[] Input)> Seeds()
    {
        var random = new Random(12345);
        for (var i = 0; i < Schemas.Count; i++)
        {
            for (var sample = 0; sample < 4; sample++)
            {
                var value = new SampleValues(random).Create(Schemas[i], 0);
                yield return (nameof(GenericBinary), [(byte)i, .. GenericDatumWriter.Create(Schemas[i]).WriteToArray(value)]);
                yield return (nameof(GenericJson), [(byte)i, .. GenericDatumJsonWriter.Create(Schemas[i]).WriteToUtf8Bytes(value)]);
            }

            yield return (nameof(SchemaParse), Encoding.UTF8.GetBytes(Schemas[i].ToJson()));
        }
    }

    private static void CheckRoundTrips(AvroSchema schema, AvroValue value)
    {
        // Binary preserves every value exactly, including NaN payloads.
        var binary = GenericDatumWriter.Create(schema).WriteToArray(value);
        var fromBinary = GenericDatumReader.Create(schema).Read(binary);
        if (!fromBinary.Equals(value))
        {
            throw new InvalidOperationException($"Binary round trip changed the value: {value} became {fromBinary}.");
        }

        // JSON has a single NaN, so compare the JSON text of two consecutive round trips instead of the values.
        var json = GenericDatumJsonWriter.Create(schema).WriteToString(value);
        var fromJson = GenericDatumJsonReader.Create(schema).Read(json);
        var jsonAgain = GenericDatumJsonWriter.Create(schema).WriteToString(fromJson);
        if (!string.Equals(json, jsonAgain, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"JSON round trip changed the value: {json} became {jsonAgain}.");
        }
    }

    /// <summary>Small, deterministic sample values for seeds.</summary>
    private sealed class SampleValues(Random random)
    {
        public AvroValue Create(AvroSchema schema, int depth) => schema switch
        {
            RecordSchema record => CreateRecord(record, depth),
            EnumSchema enumSchema => AvroValue.FromEnum(enumSchema, random.Next(enumSchema.Symbols.Count)),
            FixedSchema fixedSchema => new GenericFixed(fixedSchema, Bytes(fixedSchema.Size)),
            ArraySchema array => AvroValue.FromArray(Enumerable.Range(0, depth > 3 ? 0 : random.Next(4)).Select(_ => Create(array.Items, depth + 1)).ToList()),
            MapSchema map => AvroValue.FromMap(Enumerable.Range(0, depth > 3 ? 0 : random.Next(4)).ToDictionary(k => "k" + k, _ => Create(map.Values, depth + 1), StringComparer.Ordinal)),
            UnionSchema union => Create(depth > 3 ? union.Branches[0] : union.Branches[random.Next(union.Branches.Count)], depth + 1),
            _ => schema.Type switch
            {
                AvroSchemaType.Null => AvroValue.Null,
                AvroSchemaType.Boolean => random.Next(2) == 1,
                AvroSchemaType.Int => random.Next(int.MinValue, int.MaxValue),
                AvroSchemaType.Long => ((long)random.Next() << 32) | (uint)random.Next(),
                AvroSchemaType.Float => (float)random.NextDouble(),
                AvroSchemaType.Double => random.NextDouble() * 1e6,
                AvroSchemaType.Bytes => Bytes(random.Next(8)),
                _ => "s" + random.Next(1000),
            },
        };

        private AvroValue CreateRecord(RecordSchema record, int depth)
        {
            var value = new GenericRecord(record);
            foreach (var field in record.Fields)
            {
                value[field.Position] = Create(field.Schema, depth + 1);
            }

            return value;
        }

        private byte[] Bytes(int length)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }
    }
}
