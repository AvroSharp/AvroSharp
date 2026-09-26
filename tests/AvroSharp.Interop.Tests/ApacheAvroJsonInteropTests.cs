using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Generic;
using CsCheck;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Random schemas with random data in the JSON encoding. JSON text is not compared, because number formatting and
/// escaping legitimately differ; values and their binary encodings are.
/// </summary>
public class ApacheAvroJsonInteropTests
{
    private const int Iterations = 1_000;

    [Test]
    public async Task AvroSharpJson_IsReadByApacheAsTheSameValue()
    {
        var checkedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (json, seed) =>
            {
                var schema = AvroSchema.Parse(json);
                if (IsRecursive(schema) || new RandomValues(seed).TryCreate(schema) is not { } value)
                {
                    return;
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);
                var ourJson = GenericDatumJsonWriter.Create(schema).WriteToString(value);

                // Apache decodes AvroSharp's JSON; its binary encoding of the result must equal AvroSharp's.
                var apacheSchema = ApacheSchema.Parse(json);
                object apacheValue;
                try
                {
                    apacheValue = new ApacheReader(apacheSchema, apacheSchema).Read(null!, new Avro.IO.JsonDecoder(apacheSchema, ourJson));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Apache could not read AvroSharp's JSON.\nSchema: {json}\nJSON: {ourJson}", ex);
                }

                using var output = new MemoryStream();
                new ApacheWriter(apacheSchema).Write(apacheValue, new Avro.IO.BinaryEncoder(output));
                var ours = GenericDatumWriter.Create(schema).WriteToArray(value);
                if (!output.ToArray().AsSpan().SequenceEqual(ours))
                {
                    throw new InvalidOperationException(
                        $"Values differ.\nSchema: {json}\nJSON: {ourJson}\nAvroSharp: {Convert.ToHexString(ours)}\nApache:    {Convert.ToHexString(output.ToArray())}");
                }

                // And AvroSharp reads its own JSON back to the value it wrote.
                var read = GenericDatumJsonReader.Create(schema).Read(ourJson);
                if (!read.Equals(value))
                {
                    throw new InvalidOperationException($"Read-back differs.\nSchema: {json}\nJSON: {ourJson}\nWritten: {value}\nRead:    {read}");
                }
            },
            iter: Iterations);

        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
    }

    [Test]
    public async Task ApacheJson_IsReadByAvroSharpAsTheSameValue()
    {
        var checkedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (json, seed) =>
            {
                var schema = AvroSchema.Parse(json);
                if (IsRecursive(schema) || HasEmptyRecord(schema) || new RandomValues(seed).TryCreate(schema) is not { } value)
                {
                    return;
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);

                // Apache gets the value through the binary encoding, then writes it as JSON.
                var apacheSchema = ApacheSchema.Parse(json);
                using var input = new MemoryStream(GenericDatumWriter.Create(schema).WriteToArray(value));
                var apacheValue = new ApacheReader(apacheSchema, apacheSchema).Read(null!, new Avro.IO.BinaryDecoder(input));
                using var jsonOutput = new MemoryStream();
                var encoder = new Avro.IO.JsonEncoder(apacheSchema, jsonOutput);
                new ApacheWriter(apacheSchema).Write(apacheValue, encoder);
                encoder.Flush();
                var apacheJson = Encoding.UTF8.GetString(jsonOutput.ToArray());

                AvroValue read;
                try
                {
                    read = GenericDatumJsonReader.Create(schema).Read(apacheJson);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"AvroSharp could not read Apache's JSON.\nSchema: {json}\nJSON: {apacheJson}", ex);
                }

                if (!read.Equals(value))
                {
                    throw new InvalidOperationException($"Values differ.\nSchema: {json}\nJSON: {apacheJson}\nWritten: {value}\nRead:    {read}");
                }
            },
            iter: Iterations);

        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
    }

    /// <summary>
    /// Apache.Avro 1.12.2's <c>JsonEncoder</c> writes nothing at all for a value whose schema contains a record with no
    /// fields (not even the enclosing object), so those schemas are left out of the Apache-to-AvroSharp direction.
    /// </summary>
    private static bool HasEmptyRecord(AvroSharp.Schemas.AvroSchema schema)
    {
        return Visit(schema, []);

        static bool Visit(AvroSharp.Schemas.AvroSchema schema, System.Collections.Generic.HashSet<AvroSharp.Schemas.RecordSchema> seen) => schema switch
        {
            AvroSharp.Schemas.RecordSchema record => seen.Add(record)
                && (record.Fields.Count == 0 || System.Linq.Enumerable.Any(record.Fields, f => Visit(f.Schema, seen))),
            AvroSharp.Schemas.ArraySchema array => Visit(array.Items, seen),
            AvroSharp.Schemas.MapSchema map => Visit(map.Values, seen),
            AvroSharp.Schemas.UnionSchema union => System.Linq.Enumerable.Any(union.Branches, b => Visit(b, seen)),
            _ => false,
        };
    }

    /// <summary>
    /// Apache.Avro 1.12.2's JSON grammar generator recurses without end on a record that contains itself (a stack
    /// overflow in <c>Symbol.FlattenedSize</c>, which kills the test process), so such schemas are left out here.
    /// AvroSharp's own JSON tests cover recursive records.
    /// </summary>
    private static bool IsRecursive(AvroSharp.Schemas.AvroSchema schema)
    {
        return Visit(schema, []);

        static bool Visit(AvroSharp.Schemas.AvroSchema schema, System.Collections.Generic.HashSet<AvroSharp.Schemas.RecordSchema> open) => schema switch
        {
            AvroSharp.Schemas.RecordSchema record => !open.Add(record) || VisitFields(record, open),
            AvroSharp.Schemas.ArraySchema array => Visit(array.Items, open),
            AvroSharp.Schemas.MapSchema map => Visit(map.Values, open),
            AvroSharp.Schemas.UnionSchema union => System.Linq.Enumerable.Any(union.Branches, b => Visit(b, open)),
            _ => false,
        };

        static bool VisitFields(AvroSharp.Schemas.RecordSchema record, System.Collections.Generic.HashSet<AvroSharp.Schemas.RecordSchema> open)
        {
            foreach (var field in record.Fields)
            {
                if (Visit(field.Schema, open))
                {
                    return true;
                }
            }

            open.Remove(record);
            return false;
        }
    }
}
