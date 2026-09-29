using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tool.Tests;

/// <summary><c>avrosharp schema canonical|fingerprint</c>: results for one or several schemas, and failures.</summary>
public class SchemaCommandTests
{
    private const string Status = """{"type":"enum","name":"Status","namespace":"shop","doc":"ignored","symbols":["NEW","PAID"]}""";

    private const string Customer = """
        {"type":"record","name":"Customer","namespace":"crm","fields":[
          {"name":"name","type":"string"},{"name":"status","type":"shop.Status"}]}
        """;

    [Test]
    public async Task Canonical_OfOneSchema_IsPrintedAlone()
    {
        using var tool = new ToolRunner();
        var file = tool.Write("status.avsc", Status);

        var result = ToolRunner.Run("schema", "canonical", file);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).IsEqualTo("""{"name":"shop.Status","type":"enum","symbols":["NEW","PAID"]}""" + Environment.NewLine);
        await Assert.That(result.Error).IsEmpty();
    }

    [Test]
    public async Task Fingerprint_MatchesApachesVector_InEveryFormat()
    {
        // Vector 000 of Apache Avro's share/test/data/schema-tests.txt.
        const long Expected = 7195948357588979594;
        var littleEndian = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(littleEndian, Expected);

        var decimalForm = ToolRunner.RunWithInput("\"null\"", "schema", "fingerprint", "-", "--format", "decimal");
        var hex = ToolRunner.RunWithInput("\"null\"", "schema", "fingerprint", "-");
        var base64 = ToolRunner.RunWithInput("\"null\"", "schema", "fingerprint", "-", "-f", "base64");

        await Assert.That(decimalForm.ExitCode).IsEqualTo(0);
        await Assert.That(decimalForm.Output.Trim()).IsEqualTo(Expected.ToString(CultureInfo.InvariantCulture));
        await Assert.That(hex.Output.Trim()).IsEqualTo(Convert.ToHexStringLower(littleEndian));
        await Assert.That(base64.Output.Trim()).IsEqualTo(Convert.ToBase64String(littleEndian));
    }

    [Test]
    public async Task Fingerprint_Md5AndSha256_AreTheDigestsOfTheCanonicalForm()
    {
        using var tool = new ToolRunner();
        var file = tool.Write("status.avsc", Status);
        var schema = AvroSchema.Parse(Status);

        var md5 = ToolRunner.Run("schema", "fingerprint", file, "-a", "md5");
        var sha256 = ToolRunner.Run("schema", "fingerprint", file, "--algorithm", "sha256", "--format", "base64");

        await Assert.That(md5.ExitCode).IsEqualTo(0);
        await Assert.That(md5.Output.Trim()).IsEqualTo(Convert.ToHexStringLower(SchemaFingerprint.Md5(schema)));
        await Assert.That(sha256.Output.Trim()).IsEqualTo(Convert.ToBase64String(SchemaFingerprint.Sha256(schema)));
    }

    [Test]
    public async Task SeveralSchemas_ReferringToEachOther_ArePrintedWithTheirPaths()
    {
        using var tool = new ToolRunner();

        // The customer file comes first in path order and uses the status file's type.
        var customer = tool.Write("s/a-customer.avsc", Customer);
        var status = tool.Write("s/b-status.avsc", Status);

        var result = ToolRunner.Run("schema", "fingerprint", tool.PathOf("s"), "-f", "decimal");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var lines = result.Output.TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).StartsWith($"{customer}: ");
        await Assert.That(lines[1]).IsEqualTo($"{status}: {AvroSchema.Parse(Status).Fingerprint64.ToString(CultureInfo.InvariantCulture)}");
    }

    [Test]
    public async Task References_DefineTypes_WithoutAResultOfTheirOwn()
    {
        using var tool = new ToolRunner();
        var customer = tool.Write("s/customer.avsc", Customer);
        tool.Write("s/status.avsc", Status);

        // The reference folder holds the input too; it is parsed once.
        var result = ToolRunner.Run("schema", "canonical", customer, "-r", tool.PathOf("s"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.Trim()).IsEqualTo(
            """{"name":"crm.Customer","type":"record","fields":[{"name":"name","type":"string"},{"name":"status","type":{"name":"shop.Status","type":"enum","symbols":["NEW","PAID"]}}]}""");
    }

    [Test]
    public async Task StandardInput_IsReadForDash()
    {
        var result = ToolRunner.RunWithInput(Status, "schema", "canonical", "-");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output.Trim()).IsEqualTo("""{"name":"shop.Status","type":"enum","symbols":["NEW","PAID"]}""");
    }

    [Test]
    public async Task UndefinedReference_IsASchemaError()
    {
        using var tool = new ToolRunner();
        var customer = tool.Write("customer.avsc", Customer);

        var result = ToolRunner.Run("schema", "canonical", customer);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains($"{customer}(2,");
        await Assert.That(result.Error).Contains("error AVROGEN001: 'shop.Status' is not a defined type.");
        await Assert.That(result.Output).IsEmpty();
    }

    [Test]
    public async Task InvalidJson_FromStandardInput_IsReportedAsStdin()
    {
        var result = ToolRunner.RunWithInput("{\"type\":", "schema", "fingerprint", "-");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).StartsWith("<stdin>(");
        await Assert.That(result.Error).Contains("error AVROGEN001: The schema is not valid JSON");
    }

    [Test]
    public async Task MissingFiles_AndBadReferences_AreReported()
    {
        using var tool = new ToolRunner();
        var status = tool.Write("status.avsc", Status);
        var missing = tool.PathOf("missing.avsc");
        var bad = tool.Write("bad.avsc", """{"type":"enum","name":"E","symbols":[]}x""");

        var missingInput = ToolRunner.Run("schema", "canonical", missing);
        var badReference = ToolRunner.Run("schema", "canonical", status, "-r", bad);

        await Assert.That(missingInput.ExitCode).IsEqualTo(1);
        await Assert.That(missingInput.Error).Contains($"error: '{missing}' is not a file or folder.");
        await Assert.That(badReference.ExitCode).IsEqualTo(1);
        await Assert.That(badReference.Error).Contains($"{bad}(");
        await Assert.That(badReference.Output).IsEmpty();
    }
}
