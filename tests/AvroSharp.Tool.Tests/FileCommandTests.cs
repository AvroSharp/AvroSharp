using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Codecs;
using AvroSharp.Containers;
using AvroSharp.Generic;

namespace AvroSharp.Tool.Tests;

/// <summary><c>avrosharp file ...</c>: avro-tools' container file commands (#172).</summary>
public class FileCommandTests
{
    private static readonly AvroFileReaderOptions AllCodecs = new() { Codecs = AvroCodecs.All };

    [Test]
    public async Task ToJson_PrintsWhatAvroToolsPrints()
    {
        // weather.json is avro-tools' tojson of weather.avro, from Apache Avro's test data.
        using var tool = new ToolRunner();
        var expected = File.ReadAllText(tool.CopyTestData("apache-avro/weather.json")).ReplaceLineEndings("\n");

        var result = ToolRunner.Run("file", "tojson", tool.CopyTestData("apache-avro/weather.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.ReplaceLineEndings("\n")).IsEqualTo(expected);
    }

    [Test]
    public async Task ToJson_OfEveryLogicalType_HasJavasValues()
    {
        // logical.json is avro-tools' tojson of logical.avro. Java writes bytes as raw characters and .NET escapes them,
        // so the values are compared, not the text.
        using var tool = new ToolRunner();
        var expected = File.ReadAllLines(tool.CopyTestData("java-avro/logical.json"));

        var result = ToolRunner.Run("file", "tojson", tool.CopyTestData("java-avro/logical.avro"));
        var actual = result.Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            using var javaLine = JsonDocument.Parse(expected[i]);
            using var ourLine = JsonDocument.Parse(actual[i]);
            await Assert.That(JsonElement.DeepEquals(javaLine.RootElement, ourLine.RootElement)).IsTrue();
        }
    }

    [Test]
    public async Task ToJson_WithAReaderSchema_ResolvesTheValues()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("station.avsc", """{"type":"record","name":"Weather","namespace":"test","fields":[{"name":"station","type":"string"}]}""");

        var result = ToolRunner.Run("file", "tojson", tool.CopyTestData("apache-avro/weather.avro"), "--reader-schema", schema);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.ReplaceLineEndings("\n").Split('\n')[0]).IsEqualTo("""{"station":"011990-99999"}""");
    }

    [Test]
    public async Task FromJson_ReadsWhatToJsonPrints_PrettyOrNot()
    {
        using var tool = new ToolRunner();
        var weather = tool.CopyTestData("apache-avro/weather.avro");
        var schema = tool.Write("weather.avsc", ToolRunner.Run("file", "getschema", weather).Output);
        var pretty = tool.Write("pretty.json", ToolRunner.Run("file", "tojson", weather, "--pretty").Output);
        var output = tool.PathOf("out.avro");

        var result = ToolRunner.Run("file", "fromjson", pretty, "--schema", schema, "--codec", "zstd", "-o", output);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(ReadAll(output)).IsEquivalentTo(ReadAll(weather), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Codec(output)).IsEqualTo("zstandard");
    }

    [Test]
    public async Task FromJson_ReadsStandardInput_AndWritesStandardOutput()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("int.avsc", "\"int\"");

        var (result, file) = ToolRunner.RunWithStreams(Encoding.UTF8.GetBytes("1 2\n3"), "file", "fromjson", "-", "--schema", schema, "-o", "-");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        using var reader = AvroFileReader.OpenGeneric(new MemoryStream(file));
        await Assert.That(reader.ReadAll().Select(v => v.AsInt32()).ToList()).IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task FromJson_NamesTheValueThatDoesNotFit()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("int.avsc", "\"int\"");
        var json = tool.Write("values.json", "1\n\"two\"\n3");

        var result = ToolRunner.Run("file", "fromjson", json, "--schema", schema, "-o", tool.PathOf("out.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("Value 2");
    }

    [Test]
    public async Task GetSchema_AndGetMeta_PrintTheHeader()
    {
        using var tool = new ToolRunner();
        var file = tool.CopyTestData("java-avro/weather-zstandard.avro");

        var schema = ToolRunner.Run("file", "getschema", file);
        var meta = ToolRunner.Run("file", "getmeta", file);
        var codec = ToolRunner.Run("file", "getmeta", file, "--key", "avro.codec");
        var missing = ToolRunner.Run("file", "getmeta", file, "--key", "nothing");

        await Assert.That(schema.Output).StartsWith("""{"type":"record","name":"Weather","namespace":"test",""");
        await Assert.That(meta.Output).Contains("avro.codec\tzstandard");
        await Assert.That(codec.Output.Trim()).IsEqualTo("zstandard");
        await Assert.That(missing.ExitCode).IsEqualTo(1);
        await Assert.That(missing.Error).Contains("'nothing'");
    }

    [Test]
    public async Task Count_CountsObjects_InEveryCodec()
    {
        using var tool = new ToolRunner();
        var files = new[] { "deflate", "snappy", "xz" }.Select(codec => tool.CopyTestData($"java-avro/many-{codec}.avro")).ToArray();

        var one = ToolRunner.Run("file", "count", files[0]);
        var several = ToolRunner.Run(["file", "count", .. files]);

        await Assert.That(one.Output.Trim()).IsEqualTo("8000");
        await Assert.That(several.Output.ReplaceLineEndings("\n").Trim().Split('\n')).IsEquivalentTo(files.Select(f => $"{f}: 8000"), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Cat_WithOffsetLimitAndCodec_WritesThoseObjects()
    {
        using var tool = new ToolRunner();
        var first = tool.CopyTestData("java-avro/weather-deflate.avro");
        var second = tool.CopyTestData("java-avro/weather-snappy.avro");
        var output = tool.PathOf("out.avro");

        var result = ToolRunner.Run("file", "cat", first, second, "--offset", "3", "--limit", "4", "--codec", "xz", "-o", output);

        var all = ReadAll(first).Concat(ReadAll(second)).ToList();
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(ReadAll(output)).IsEquivalentTo(all.Skip(3).Take(4), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Codec(output)).IsEqualTo("xz");
    }

    [Test]
    public async Task Cat_AndConcat_WithoutOptions_CopyBlocks()
    {
        using var tool = new ToolRunner();
        var deflate = tool.CopyTestData("java-avro/weather-deflate.avro");
        var snappy = tool.CopyTestData("java-avro/weather-snappy.avro");

        var cat = ToolRunner.Run("file", "cat", deflate, deflate, "-o", tool.PathOf("cat.avro"));
        var concat = ToolRunner.Run("file", "concat", deflate, snappy, "-o", tool.PathOf("concat.avro"));

        await Assert.That(cat.ExitCode).IsEqualTo(0);
        await Assert.That(concat.ExitCode).IsEqualTo(0);
        await Assert.That(Blocks(tool.PathOf("cat.avro"))).IsEquivalentTo(Blocks(deflate).Concat(Blocks(deflate)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Codec(tool.PathOf("concat.avro"))).IsEqualTo("deflate");
        await Assert.That(ReadAll(tool.PathOf("concat.avro"))).IsEquivalentTo(ReadAll(deflate).Concat(ReadAll(snappy)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Concat_RejectsAnotherSchema()
    {
        using var tool = new ToolRunner();
        var weather = tool.CopyTestData("apache-avro/weather.avro");
        var logical = tool.CopyTestData("java-avro/logical.avro");

        var result = ToolRunner.Run("file", "concat", weather, logical, "-o", tool.PathOf("out.avro"));
        var cat = ToolRunner.Run("file", "cat", weather, logical, "--limit", "100", "-o", tool.PathOf("cat.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("Input 1");
        await Assert.That(cat.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task Recodec_RecompressesEveryBlock_AndKeepsTheValues()
    {
        using var tool = new ToolRunner();
        var input = tool.CopyTestData("java-avro/many-deflate.avro");
        var output = tool.PathOf("out.avro");

        var result = ToolRunner.Run("file", "recodec", input, "--codec", "xz", "-o", output);
        var leveled = ToolRunner.Run("file", "recodec", input, "--codec", "deflate", "--level", "9", "-o", tool.PathOf("smallest.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Codec(output)).IsEqualTo("xz");
        await Assert.That(ReadAll(output)).IsEquivalentTo(ReadAll(input), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(leveled.ExitCode).IsEqualTo(0);
        await Assert.That(ReadAll(tool.PathOf("smallest.avro")).Count).IsEqualTo(8000);
    }

    [Test]
    [Arguments("--codec", "null", "--level", "3")]
    [Arguments("--level", "3")]
    [Arguments("--codec", "xz", "--level", "10")]
    [Arguments("--codec", "brotli")]
    public async Task CodecOptions_ThatDoNotFit_AreUsageErrors(params string[] options)
    {
        using var tool = new ToolRunner();

        var result = ToolRunner.Run(["file", "recodec", tool.CopyTestData("apache-avro/weather.avro"), "-o", tool.PathOf("out.avro"), .. options]);

        await Assert.That(result.ExitCode).IsEqualTo(2);
    }

    [Test]
    public async Task Random_WritesTheSameObjectsForASeed_WithXzAtItsDefaultLevel()
    {
        using var tool = new ToolRunner();
        var schema = tool.CopyTestData("java-avro/logical.avsc");

        var first = ToolRunner.Run("file", "random", "--schema", schema, "--count", "50", "--seed", "3", "--codec", "xz", "-o", tool.PathOf("a.avro"));
        var second = ToolRunner.Run("file", "random", "--schema", schema, "-n", "50", "--seed", "3", "-o", tool.PathOf("b.avro"));

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(second.ExitCode).IsEqualTo(0);
        await Assert.That(ReadAll(tool.PathOf("a.avro")).Count).IsEqualTo(50);
        await Assert.That(ReadAll(tool.PathOf("a.avro"))).IsEquivalentTo(ReadAll(tool.PathOf("b.avro")), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Repair_KeepsTheBlocksThatReadWhole()
    {
        // many-deflate.avro has three blocks; damage the first two.
        using var tool = new ToolRunner();
        var original = tool.CopyTestData("java-avro/many-deflate.avro");
        var data = File.ReadAllBytes(original);
        foreach (var offset in new[] { data.Length / 3, 2 * data.Length / 3 })
        {
            for (var i = offset; i < offset + 40; i++)
            {
                data[i] ^= 0x5A;
            }
        }

        var damaged = tool.PathOf("damaged.avro");
        File.WriteAllBytes(damaged, data);
        var output = tool.PathOf("repaired.avro");

        var result = ToolRunner.Run("file", "repair", damaged, "-o", output);
        var whole = ToolRunner.Run("file", "repair", original, "-o", tool.PathOf("whole.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Error).Contains("skipped 2 damaged regions");
        await Assert.That(ReadAll(output)).IsEquivalentTo(ReadAll(original).Skip(8000 - 1849), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(whole.Error).Contains("The file is whole: 8000 objects in 3 blocks.");
    }

    [Test]
    public async Task AMissingFile_IsAnError()
    {
        using var tool = new ToolRunner();

        var result = ToolRunner.Run("file", "tojson", tool.PathOf("missing.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).StartsWith("error: ");
    }

    /// <summary>JSON deeper than System.Text.Json's default 64 levels, as tojson writes a long recursive value.</summary>
    [Test]
    public async Task FromJson_ReadsDeepValues_AsToJsonWritesThem()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("node.avsc", """{"type":"record","name":"Node","fields":[{"name":"v","type":"int"},{"name":"next","type":["null","Node"]}]}""");
        var json = "null";
        for (var i = 0; i < 60; i++)
        {
            json = i == 0 ? "{\"v\":0,\"next\":null}" : "{\"v\":" + i + ",\"next\":{\"Node\":" + json + "}}";
        }

        var result = ToolRunner.Run("file", "fromjson", tool.Write("deep.json", json), "--schema", schema, "-o", tool.PathOf("deep.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(ToolRunner.Run("file", "tojson", tool.PathOf("deep.avro")).Output.Trim()).IsEqualTo(json);
    }

    /// <summary>A failed command leaves an existing output as it was, even when it is the input (repair x -o x).</summary>
    [Test]
    public async Task AFailedCommand_LeavesTheOutputAsItWas()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("int.avsc", "\"int\"");
        var output = tool.Write("out.avro", "keep me");
        var damaged = tool.Write("damaged.avro", "not a container");

        var fromJson = ToolRunner.Run("file", "fromjson", tool.Write("values.json", "1 2 \"three\""), "--schema", schema, "-o", output);
        var repair = ToolRunner.Run("file", "repair", damaged, "-o", damaged);

        await Assert.That(fromJson.ExitCode).IsEqualTo(1);
        await Assert.That(File.ReadAllText(output)).IsEqualTo("keep me");
        await Assert.That(repair.ExitCode).IsEqualTo(1);
        await Assert.That(File.ReadAllText(damaged)).IsEqualTo("not a container");
        await Assert.That(Directory.GetFiles(tool.Folder, "*.tmp")).IsEmpty();
    }

    /// <summary>cat --limit stops at the limit: a damaged block after it is never read.</summary>
    [Test]
    public async Task Cat_StopsAtTheLimit()
    {
        using var tool = new ToolRunner();
        var original = tool.CopyTestData("java-avro/many-deflate.avro");
        var data = File.ReadAllBytes(original);
        for (var i = data.Length / 2; i < (data.Length / 2) + 40; i++)
        {
            data[i] ^= 0x5A;
        }

        var damaged = tool.PathOf("damaged.avro");
        File.WriteAllBytes(damaged, data);

        var result = ToolRunner.Run("file", "cat", damaged, "--limit", "10", "-o", tool.PathOf("out.avro"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(ReadAll(tool.PathOf("out.avro")).Count).IsEqualTo(10);
    }

    private static List<AvroValue> ReadAll(string path)
    {
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(path), options: AllCodecs);
        return reader.ReadAll().ToList();
    }

    private static string Codec(string path)
    {
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(path), options: AllCodecs);
        return reader.Codec.Name;
    }

    // Each block's count and data, as hex.
    private static List<string> Blocks(string path)
    {
        using var reader = AvroFileReader.OpenGeneric(File.OpenRead(path), options: AllCodecs);
        var blocks = new List<string>();
        while (reader.TryReadBlock(out var block))
        {
            blocks.Add($"{block.Count}:{Convert.ToHexString(block.Data.Span)}");
        }

        return blocks;
    }
}
