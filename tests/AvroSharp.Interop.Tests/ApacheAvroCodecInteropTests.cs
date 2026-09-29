using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;
using ApacheBzip2Codec = Avro.File.BZip2.BZip2Codec;
using ApacheCodec = Avro.File.Codec;
using ApacheFileReader = Avro.File.DataFileReader<Avro.Generic.GenericRecord>;
using ApacheFileWriter = Avro.File.DataFileWriter<Avro.Generic.GenericRecord>;
using ApacheRecord = Avro.Generic.GenericRecord;
using ApacheRecordSchema = Avro.RecordSchema;
using ApacheSchema = Avro.Schema;
using ApacheSnappyCodec = Avro.File.Snappy.SnappyCodec;
using ApacheWriter = Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// The snappy and bzip2 codecs of AvroSharp.Codecs against Apache.Avro's managed codec packages, in both directions,
/// with several blocks per file. (Apache's xz and zstandard packages wrap native libraries; those codecs are checked
/// against files written by Apache Avro Java instead, in AvroSharp.Codecs.Tests.)
/// </summary>
public class ApacheAvroCodecInteropTests
{
    private const string Json =
        """{"type":"record","name":"Row","namespace":"test","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"},{"name":"data","type":"bytes"}]}""";

    private static readonly RecordSchema s_schema = (RecordSchema)AvroSchema.Parse(Json);
    private static readonly ApacheRecordSchema s_apacheSchema = (ApacheRecordSchema)ApacheSchema.Parse(Json);
    private static int s_registered;

    public static IEnumerable<string> Codecs() => ["snappy", "bzip2"];

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task FilesWrittenByAvroSharp_AreReadByApache(string codec)
    {
        RegisterApacheCodecs();
        using var file = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(file, s_schema, new AvroFileWriterOptions { Codec = Ours(codec), SyncInterval = 2000, LeaveOpen = true }))
        {
            for (var i = 0; i < 1000; i++)
            {
                writer.Write(new GenericRecord(s_schema) { ["id"] = (long)i, ["name"] = Name(i), ["data"] = Data(i) });
            }
        }

        file.Position = 0;
        using var reader = ApacheFileReader.OpenReader(file, s_apacheSchema);
        var rows = new List<ApacheRecord>();
        while (reader.HasNext())
        {
            rows.Add(reader.Next());
        }

        await Assert.That(System.Text.Encoding.UTF8.GetString(reader.GetMeta("avro.codec"))).IsEqualTo(codec);
        await Assert.That(rows.Count).IsEqualTo(1000);
        for (var i = 0; i < rows.Count; i++)
        {
            await Assert.That((long)rows[i]["id"]).IsEqualTo(i);
            await Assert.That((string)rows[i]["name"]).IsEqualTo(Name(i));
            await Assert.That(((byte[])rows[i]["data"]).SequenceEqual(Data(i))).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(nameof(Codecs))]
    public async Task FilesWrittenByApache_AreReadByAvroSharp(string codec)
    {
        ApacheCodec apacheCodec = string.Equals(codec, "snappy", StringComparison.Ordinal) ? new ApacheSnappyCodec() : new ApacheBzip2Codec();
        using var file = new MemoryStream();
        using (var writer = ApacheFileWriter.OpenWriter(new ApacheWriter(s_apacheSchema), file, apacheCodec, leaveOpen: true))
        {
            writer.SetSyncInterval(2000);
            for (var i = 0; i < 1000; i++)
            {
                var row = new ApacheRecord(s_apacheSchema);
                row.Add("id", (long)i);
                row.Add("name", Name(i));
                row.Add("data", Data(i));
                writer.Append(row);
            }
        }

        file.Position = 0;
        using var reader = AvroFileReader.OpenGeneric(file, options: new AvroFileReaderOptions { Codecs = AvroCodecs.All });
        var rows = reader.ReadAll().Select(v => v.AsRecord()).ToList();

        await Assert.That(reader.Codec.Name).IsEqualTo(codec);
        await Assert.That(rows.Count).IsEqualTo(1000);
        for (var i = 0; i < rows.Count; i++)
        {
            await Assert.That(rows[i]["id"].AsInt64()).IsEqualTo(i);
            await Assert.That(rows[i]["name"].AsString()).IsEqualTo(Name(i));
            await Assert.That(rows[i]["data"].AsBytes().SequenceEqual(Data(i))).IsTrue();
        }
    }

    private static AvroCodec Ours(string codec) => string.Equals(codec, "snappy", StringComparison.Ordinal) ? SnappyCodec.Default : Bzip2Codec.Default;

    private static string Name(int i) => $"row {i % 23} of the interop test";

    private static byte[] Data(int i) => BitConverter.GetBytes(i * 2654435761L);

    /// <summary>Apache's reader finds codecs other than null and deflate through global resolvers; register ours once.</summary>
    private static void RegisterApacheCodecs()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            ApacheCodec.RegisterResolver(name => name switch
            {
                "snappy" => new ApacheSnappyCodec(),
                "bzip2" => new ApacheBzip2Codec(),
                _ => null,
            });
        }
    }
}
