using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Containers;
using AvroSharp.Generic;
using CsCheck;
using ApacheCodec = Avro.File.Codec;
using ApacheFileReader = Avro.File.DataFileReader<object>;
using ApacheFileWriter = Avro.File.DataFileWriter<object>;
using ApacheReader = Avro.Generic.GenericDatumReader<object>;
using ApacheSchema = Avro.Schema;
using ApacheWriter = Avro.Generic.GenericDatumWriter<object>;
using AvroSchema = AvroSharp.Schemas.AvroSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// Object container files exchanged with Apache.Avro in both directions, for random schemas and data, with the null
/// and deflate codecs and sync intervals small enough to give several blocks per file.
/// </summary>
public class ApacheAvroContainerInteropTests
{
    private const int Iterations = 300;

    [Test]
    public async Task FilesWrittenByAvroSharp_AreReadByApache()
    {
        var checkedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (json, seed) =>
            {
                var schema = AvroSchema.Parse(json);
                if (CreateValues(schema, seed) is not { } values)
                {
                    return;
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);
                var deflate = (seed & 1) == 0;
                using var file = new MemoryStream();
                using (var writer = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions
                {
                    Codec = deflate ? AvroCodec.Deflate : AvroCodec.Null,
                    SyncInterval = 1 + (seed & 0xFF),
                    LeaveOpen = true,
                }))
                {
                    foreach (var value in values)
                    {
                        writer.Write(value);
                    }
                }

                file.Position = 0;
                var apacheSchema = ApacheSchema.Parse(json);
                using var reader = ApacheFileReader.OpenReader(file, apacheSchema);
                var datumWriter = new ApacheWriter(apacheSchema);
                var ours = GenericDatumWriter.Create(schema);
                var index = 0;
                while (reader.HasNext())
                {
                    using var output = new MemoryStream();
                    datumWriter.Write(reader.Next(), new Avro.IO.BinaryEncoder(output));
                    var expected = ours.WriteToArray(values[index++]);
                    if (!output.ToArray().AsSpan().SequenceEqual(expected))
                    {
                        throw new InvalidOperationException($"Object {index - 1} differs.\nSchema: {json}\nAvroSharp: {Convert.ToHexString(expected)}\nApache:    {Convert.ToHexString(output.ToArray())}");
                    }
                }

                if (index != values.Count)
                {
                    throw new InvalidOperationException($"Apache read {index} of {values.Count} objects.\nSchema: {json}");
                }

                var codec = System.Text.Encoding.UTF8.GetString(reader.GetMeta("avro.codec"));
                if (!string.Equals(codec, deflate ? "deflate" : "null", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Apache sees the codec as '{codec}'.");
                }
            },
            iter: Iterations);

        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
    }

    [Test]
    public async Task FilesWrittenByApache_AreReadByAvroSharp()
    {
        var checkedSamples = 0;
        Gen.Select(RandomSchemas.ApacheCompatibleJsonWithoutLogicalTypes, Gen.Int).Sample(
            (json, seed) =>
            {
                var schema = AvroSchema.Parse(json);
                if (CreateValues(schema, seed) is not { } values)
                {
                    return;
                }

                System.Threading.Interlocked.Increment(ref checkedSamples);
                var ours = GenericDatumWriter.Create(schema);
                var encoded = values.Select(v => ours.WriteToArray(v)).ToList();

                // Apache writes the file from values it decoded from AvroSharp's bytes.
                var apacheSchema = ApacheSchema.Parse(json);
                var datumReader = new ApacheReader(apacheSchema, apacheSchema);
                using var file = new MemoryStream();
                var codec = ApacheCodec.CreateCodec((seed & 1) == 0 ? ApacheCodec.Type.Deflate : ApacheCodec.Type.Null);
                using (var writer = ApacheFileWriter.OpenWriter(new ApacheWriter(apacheSchema), file, codec, leaveOpen: true))
                {
                    writer.SetSyncInterval(32 + (seed & 0xFF));
                    writer.SetMeta("app.seed", seed);
                    foreach (var bytes in encoded)
                    {
                        writer.Append(datumReader.Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(bytes))));
                    }
                }

                file.Position = 0;
                using var reader = AvroFileReader.OpenGeneric(file);
                var read = reader.ReadAll().ToList();
                if (read.Count != values.Count)
                {
                    throw new InvalidOperationException($"AvroSharp read {read.Count} of {values.Count} objects.\nSchema: {json}");
                }

                for (var i = 0; i < read.Count; i++)
                {
                    var bytes = GenericDatumWriter.Create(reader.WriterSchema).WriteToArray(read[i]);
                    if (!bytes.AsSpan().SequenceEqual(encoded[i]))
                    {
                        throw new InvalidOperationException($"Object {i} differs.\nSchema: {json}\nExpected: {Convert.ToHexString(encoded[i])}\nRead:     {Convert.ToHexString(bytes)}");
                    }
                }

                if (!reader.TryGetMetadataString("app.seed", out var written) || !string.Equals(written, seed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Apache's metadata was not read.");
                }
            },
            iter: Iterations);

        await Assert.That(checkedSamples).IsGreaterThan(Iterations / 2);
    }

    [Test]
    [Arguments("weather.avro")]
    [Arguments("weather-sorted.avro")]
    [Arguments("syncInMeta.avro")]
    public async Task ApacheTestFiles_AreReadLikeApache(string file)
    {
        var bytes = ReadTestFile(file);

        using var ours = AvroFileReader.OpenGeneric(new MemoryStream(bytes));
        var writer = GenericDatumWriter.Create(ours.WriterSchema);
        var oursBytes = ours.ReadAll().Select(v => Convert.ToHexString(writer.WriteToArray(v))).ToList();

        using var apache = ApacheFileReader.OpenReader(new MemoryStream(bytes));
        var apacheWriter = new ApacheWriter(apache.GetSchema());
        var apacheBytes = new List<string>();
        while (apache.HasNext())
        {
            using var output = new MemoryStream();
            apacheWriter.Write(apache.Next(), new Avro.IO.BinaryEncoder(output));
            apacheBytes.Add(Convert.ToHexString(output.ToArray()));
        }

        await Assert.That(oursBytes.Count).IsGreaterThan(0);
        await Assert.That(oursBytes.SequenceEqual(apacheBytes)).IsTrue();
    }

    private static byte[] ReadTestFile(string file) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "apache-avro", file));

    // Between 0 and 40 values; null when the schema has no values.
    private static List<AvroValue>? CreateValues(AvroSchema schema, int seed)
    {
        var count = (int)((uint)seed % 41);
        var values = new List<AvroValue>(count);
        for (var i = 0; i < count; i++)
        {
            if (new RandomValues(seed + i).TryCreate(schema) is not { } value)
            {
                return null;
            }

            values.Add(value);
        }

        return values;
    }
}
