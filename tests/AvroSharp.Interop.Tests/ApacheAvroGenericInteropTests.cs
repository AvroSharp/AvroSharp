using System;
using System.IO;
using System.Threading.Tasks;
using AvroSharp.Generic;
using CsCheck;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Random schemas with random data: AvroSharp's generic writer must produce the bytes Apache.Avro (C#) produces for
/// the same data, and AvroSharp's generic reader must read them back to the same value.
/// </summary>
public class ApacheAvroGenericInteropTests
{
    private const int Iterations = 1_500;

    [Test]
    public async Task RandomData_RoundTripsThroughApacheToTheSameBytes()
    {
        var checkedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (json, seed) =>
            {
                var schema = AvroSchema.Parse(json);
                if (new RandomValues(seed).TryCreate(schema) is not { } value)
                {
                    return;
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);
                var ours = GenericDatumWriter.Create(schema).WriteToArray(value);

                // Apache reads AvroSharp's bytes and writes them again: the bytes must be identical.
                var apacheSchema = ApacheSchema.Parse(json);
                using var input = new MemoryStream(ours);
                var apacheValue = new ApacheReader(apacheSchema, apacheSchema).Read(null!, new Avro.IO.BinaryDecoder(input));
                if (input.Position != input.Length)
                {
                    throw new InvalidOperationException($"Apache left {input.Length - input.Position} byte(s) unread.\nSchema: {json}");
                }

                using var output = new MemoryStream();
                new ApacheWriter(apacheSchema).Write(apacheValue, new Avro.IO.BinaryEncoder(output));
                if (!output.ToArray().AsSpan().SequenceEqual(ours))
                {
                    throw new InvalidOperationException(
                        $"Bytes differ.\nSchema: {json}\nAvroSharp: {Convert.ToHexString(ours)}\nApache:    {Convert.ToHexString(output.ToArray())}");
                }

                // AvroSharp reads its own bytes back to the value it wrote.
                var read = GenericDatumReader.Create(schema).Read(ours);
                if (!read.Equals(value))
                {
                    throw new InvalidOperationException($"Read-back differs.\nSchema: {json}\nWritten: {value}\nRead:    {read}");
                }
            },
            iter: Iterations);

        // Guards against a generator that only produces uninhabited schemas.
        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
    }
}
