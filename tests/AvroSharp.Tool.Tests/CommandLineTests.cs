using System.Threading.Tasks;

namespace AvroSharp.Tool.Tests;

/// <summary>Help, version, and usage errors: exit code 2 and a pointer to the command's help.</summary>
public class CommandLineTests
{
    [Test]
    public async Task Help_ListsTheCommands_AndExitsZero()
    {
        var result = ToolRunner.Run("--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("avrosharp [command] [options]");
        await Assert.That(result.Output).Contains("gen <inputs>");
        await Assert.That(result.Output).Contains("schema");
        await Assert.That(result.Output).Contains("Examples:");
        await Assert.That(result.Error).IsEmpty();
    }

    [Test]
    [Arguments("gen", "--output <output> (REQUIRED)", "--namespace", "--logical-types <native|raw>", "--property-names <avro|pascal>", "--apache-compatible", "--no-nullable", "--no-date-only", "--language-version")]
    [Arguments("schema canonical", "avrosharp schema canonical <inputs>... [options]", "--reference", "standard input", "", "", "", "", "")]
    [Arguments("schema fingerprint", "--algorithm <crc64|md5|sha256>", "--format <base64|decimal|hex>", "--reference", "", "", "", "", "")]
    public async Task CommandHelp_DescribesEveryOption(string command, string a, string b, string c, string d, string e, string f, string g, string h)
    {
        var result = ToolRunner.Run([.. command.Split(' '), "--help"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        foreach (var expected in new[] { a, b, c, d, e, f, g, h })
        {
            await Assert.That(result.Output).Contains(expected);
        }
    }

    [Test]
    public async Task Version_IsPrinted()
    {
        var result = ToolRunner.Run("--version");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.Trim()).IsNotEmpty();
    }

    [Test]
    [Arguments(new string[0], "Required command was not provided.", "avrosharp --help")]
    [Arguments(new[] { "generate" }, "Unrecognized command or argument 'generate'.", "avrosharp --help")]
    [Arguments(new[] { "gen", "a.avsc" }, "Option '--output' is required.", "avrosharp gen --help")]
    [Arguments(new[] { "gen", "-o", "out" }, "Required argument missing for command: 'gen'.", "avrosharp gen --help")]
    [Arguments(new[] { "gen", "a.avsc", "-o", "out", "--bogus" }, "Unrecognized command or argument '--bogus'.", "avrosharp gen --help")]
    [Arguments(new[] { "gen", "a.avsc", "-o", "out", "--logical-types", "iso" }, "Argument 'iso' not recognized. Must be one of:", "avrosharp gen --help")]
    [Arguments(new[] { "gen", "a.avsc", "-o", "out", "--language-version", "6" }, "--language-version must be 7 or later.", "avrosharp gen --help")]
    [Arguments(new[] { "gen", "a.avsc", "-o", "out", "--language-version", "x" }, "Cannot parse argument 'x'", "avrosharp gen --help")]
    [Arguments(new[] { "schema" }, "Required command was not provided.", "avrosharp schema --help")]
    [Arguments(new[] { "schema", "canonical" }, "Required argument missing for command: 'canonical'.", "avrosharp schema canonical --help")]
    [Arguments(new[] { "schema", "fingerprint", "a.avsc", "-a", "sha1" }, "Argument 'sha1' not recognized. Must be one of:", "avrosharp schema fingerprint --help")]
    [Arguments(new[] { "schema", "fingerprint", "a.avsc", "-a", "md5", "-f", "decimal" }, "--format decimal needs --algorithm crc64.", "avrosharp schema fingerprint --help")]
    [Arguments(new[] { "schema", "canonical", "-", "a.avsc" }, "'-' (standard input) cannot be combined with other inputs.", "avrosharp schema canonical --help")]
    public async Task InvalidCommandLines_AreUsageErrors(string[] args, string message, string help)
    {
        var result = ToolRunner.Run(args);

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Error).Contains("error: " + message);
        await Assert.That(result.Error).Contains($"Run '{help}' for usage.");
        await Assert.That(result.Output).IsEmpty();
    }
}
