using System.Text.Json;
using System.Threading.Tasks;

namespace AvroSharp.Tool.Tests;

/// <summary><c>avrosharp schema compat</c> (#165): verdicts as exit codes, text and JSON output, levels, failures.</summary>
public class CompatCommandTests
{
    private const string V1 = """{"type":"record","name":"Order","namespace":"shop","fields":[{"name":"id","type":"long"}]}""";

    private const string V2 = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"id","type":"long"},{"name":"note","type":"string","default":""}]}
        """;

    private const string V3 = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"id","type":"long"},{"name":"note","type":"string"}]}
        """;

    [Test]
    public async Task CompatibleSchemas_ExitWithZero()
    {
        using var tool = new ToolRunner();

        var result = ToolRunner.Run("schema", "compat", tool.Write("v1.avsc", V1), tool.Write("v2.avsc", V2));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.Trim()).IsEqualTo("Compatible.");
        await Assert.That(result.Error).IsEmpty();
    }

    [Test]
    public async Task IncompatibleSchemas_ExitWithFour_AndPrintEveryIssue()
    {
        using var tool = new ToolRunner();

        var result = ToolRunner.Run("schema", "compat", tool.Write("v1.avsc", V1), tool.Write("v3.avsc", V3));

        await Assert.That(result.ExitCode).IsEqualTo(CompatCommand.Incompatible);
        await Assert.That(result.Output).StartsWith("Incompatible.").And.Contains("  $.note: The reader's field 'shop.Order.note' is not in the writer's record shop.Order and has no default value.");
    }

    [Test]
    public async Task APartialVerdict_ExitsWithThree_OrZeroWithAllowPartial()
    {
        using var tool = new ToolRunner();
        var writer = tool.Write("abc.avsc", """{"type":"enum","name":"E","symbols":["A","B","C"]}""");
        var reader = tool.Write("ab.avsc", """{"type":"enum","name":"E","symbols":["A","B"]}""");

        var partial = ToolRunner.Run("schema", "compat", writer, reader);
        var allowed = ToolRunner.Run("schema", "compat", writer, reader, "--allow-partial");

        await Assert.That(partial.ExitCode).IsEqualTo(CompatCommand.Partial);
        await Assert.That(partial.Output).Contains("[C]");
        await Assert.That(allowed.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Strict_FailsOnWarnings()
    {
        using var tool = new ToolRunner();
        var writer = tool.Write("w.avsc", """{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""");
        var reader = tool.Write("r.avsc", """{"type":"bytes","logicalType":"decimal","precision":5,"scale":4}""");

        var lenient = ToolRunner.Run("schema", "compat", writer, reader);
        var strict = ToolRunner.Run("schema", "compat", writer, reader, "--strict");

        await Assert.That(lenient.ExitCode).IsEqualTo(0);
        await Assert.That(lenient.Output).Contains("warning $: A decimal of scale 2 is read with scale 4");
        await Assert.That(strict.ExitCode).IsEqualTo(CompatCommand.Incompatible);
    }

    [Test]
    public async Task Json_HasTheVerdictIssuesAndWarnings()
    {
        using var tool = new ToolRunner();

        var result = ToolRunner.Run("schema", "compat", tool.Write("v1.avsc", V1), tool.Write("v3.avsc", V3), "--json");

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        await Assert.That(root.GetProperty("verdict").GetString()).IsEqualTo("incompatible");
        await Assert.That(root.GetProperty("compatible").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("issues")[0].GetProperty("kind").GetString()).IsEqualTo("MissingDefault");
        await Assert.That(root.GetProperty("issues")[0].GetProperty("path").GetString()).IsEqualTo("$.note");
        await Assert.That(root.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ALevel_ChecksTheLastSchemaAgainstTheEarlierOnes()
    {
        using var tool = new ToolRunner();
        var (v1, v2, v3) = (tool.Write("v1.avsc", V1), tool.Write("v2.avsc", V2), tool.Write("v3.avsc", V3));

        var latest = ToolRunner.Run("schema", "compat", "--level", "backward", v1, v2, v3);
        var transitive = ToolRunner.Run("schema", "compat", "--level", "backward-transitive", v1, v2, v3, "--json");

        await Assert.That(latest.ExitCode).IsEqualTo(0);
        await Assert.That(latest.Output).Contains($"Reading {v2}'s data with {v3}: Compatible.");
        await Assert.That(transitive.ExitCode).IsEqualTo(CompatCommand.Incompatible);
        using var json = JsonDocument.Parse(transitive.Output);
        var failed = json.RootElement.GetProperty("checks")[0];
        await Assert.That(json.RootElement.GetProperty("level").GetString()).IsEqualTo("backward-transitive");
        await Assert.That(failed.GetProperty("version").GetString()).IsEqualTo(v1);
        await Assert.That(failed.GetProperty("direction").GetString()).IsEqualTo("backward");
        await Assert.That(failed.GetProperty("verdict").GetString()).IsEqualTo("incompatible");
    }

    [Test]
    public async Task References_DefineNamedTypesTheSchemasUse()
    {
        using var tool = new ToolRunner();
        var status = tool.Write("refs/status.avsc", """{"type":"enum","name":"Status","namespace":"shop","symbols":["NEW","PAID"]}""");
        var writer = tool.Write("w.avsc", """{"type":"record","name":"Order","namespace":"shop","fields":[{"name":"status","type":"Status"}]}""");
        var reader = tool.Write("r.avsc", """{"type":"record","name":"Order","namespace":"shop","fields":[{"name":"status","type":"Status"},{"name":"n","type":"int","default":0}]}""");

        var result = ToolRunner.Run("schema", "compat", writer, reader, "-r", status);

        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task BadInput_FailsWithOne_AndBadUsageWithTwo()
    {
        using var tool = new ToolRunner();
        var v1 = tool.Write("v1.avsc", V1);
        var broken = tool.Write("broken.avsc", """{"type":"record","name":"Order","fields":[""");

        var missing = ToolRunner.Run("schema", "compat", v1, tool.PathOf("missing.avsc"));
        var folder = ToolRunner.Run("schema", "compat", v1, tool.Folder);
        var invalid = ToolRunner.Run("schema", "compat", v1, broken);
        var three = ToolRunner.Run("schema", "compat", v1, v1, v1);
        var one = ToolRunner.Run("schema", "compat", v1);
        var level = ToolRunner.Run("schema", "compat", "--level", "sideways", v1, v1);

        await Assert.That(missing.ExitCode).IsEqualTo(1);
        await Assert.That(missing.Error).Contains("is not a file");
        await Assert.That(folder.ExitCode).IsEqualTo(1);
        await Assert.That(folder.Error).Contains("is a folder");
        await Assert.That(invalid.ExitCode).IsEqualTo(1);
        await Assert.That(invalid.Error).Contains("broken.avsc(");
        await Assert.That(three.ExitCode).IsEqualTo(2);
        await Assert.That(three.Error).Contains("use --level");
        await Assert.That(one.ExitCode).IsEqualTo(2);
        await Assert.That(level.ExitCode).IsEqualTo(2);
    }
}
