using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;

namespace AvroSharp.Tool.Tests;

/// <summary><c>avrosharp gen</c>: the files it writes, its options, and its failures (exit code 1, nothing written).</summary>
public class GenCommandTests
{
    // a.avsc uses a type that z.avsc defines: files are parsed in path order, so this needs the retry.
    private const string Order = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"order_id","type":"long"},
          {"name":"status","type":"shop.Status"},
          {"name":"day","type":{"type":"int","logicalType":"date"}},
          {"name":"note","type":["null","string"],"default":null}
        ]}
        """;

    private const string Status = """{"type":"enum","name":"Status","namespace":"shop","symbols":["NEW","PAID"]}""";

    private const string NoNamespace = """{"type":"record","name":"Plain","fields":[{"name":"id","type":"long"}]}""";

    [Test]
    public async Task Gen_WritesOneFilePerType_FromFilesAndFolders_InAnyOrder()
    {
        using var tool = new ToolRunner();
        var order = tool.Write("schemas/a.avsc", Order);
        tool.Write("schemas/nested/z.avsc", Status);
        var plain = tool.Write("other/plain.avsc", NoNamespace);
        var output = tool.PathOf("out");

        // A folder, a file inside it named again (read once), and a file elsewhere.
        var result = ToolRunner.Run("gen", tool.PathOf("schemas"), order, plain, "-o", output, "--namespace", "Acme");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Error).IsEmpty();
        await Assert.That(result.Output).Contains($"Generated 3 file(s) from 3 schema file(s) in {output}.");

        // In folders for their namespaces; the type without an Avro namespace is in the --namespace one.
        await Assert.That(Files(output)).IsEquivalentTo(new[] { "Acme/Plain.g.cs", "shop/Order.g.cs", "shop/Status.g.cs" }, CollectionOrdering.Any);

        var orderCode = File.ReadAllText(tool.PathOf("out/shop/Order.g.cs"));
        await Assert.That(orderCode).Contains("public long OrderId { get; set; }");
        await Assert.That(orderCode).Contains("public global::System.DateOnly Day { get; set; }");
        await Assert.That(orderCode).Contains("#nullable enable");
        await Assert.That(File.ReadAllText(tool.PathOf("out/Acme/Plain.g.cs"))).Contains("namespace Acme");
    }

    [Test]
    public async Task Gen_Options_ReachTheGeneratedCode()
    {
        using var tool = new ToolRunner();
        tool.Write("s/order.avsc", Order);
        tool.Write("s/status.avsc", Status);
        var output = tool.PathOf("out");

        var result = ToolRunner.Run(
            "gen", tool.PathOf("s"), "-o", output, "--logical-types", "raw", "--property-names", "avro", "--no-nullable", "--language-version", "7");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var code = File.ReadAllText(tool.PathOf("out/shop/Order.g.cs"));
        await Assert.That(code).Contains("public long order_id { get; set; }");
        await Assert.That(code).Contains("public int day { get; set; }");
        await Assert.That(code).DoesNotContain("#nullable enable");
        await Assert.That(code).DoesNotContain("??=");
    }

    /// <summary>C# 7 has no nullable annotations, so --language-version 7 implies --no-nullable (#134).</summary>
    [Test]
    public async Task Gen_LanguageVersion7_ImpliesNoNullable()
    {
        using var tool = new ToolRunner();
        tool.Write("s/order.avsc", Order);
        tool.Write("s/status.avsc", Status);

        var result = ToolRunner.Run("gen", tool.PathOf("s"), "-o", tool.PathOf("out"), "--language-version", "7");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(tool.PathOf("out/shop/Order.g.cs"))).DoesNotContain("#nullable enable");
    }

    [Test]
    public async Task Gen_NoDateOnly_AndApacheCompatible_ReachTheGeneratedCode()
    {
        using var tool = new ToolRunner();
        tool.Write("s/order.avsc", Order);
        tool.Write("s/status.avsc", Status);

        var dates = ToolRunner.Run("gen", tool.PathOf("s"), "-o", tool.PathOf("dates"), "--no-date-only");
        var apache = ToolRunner.Run("gen", tool.PathOf("s"), "-o", tool.PathOf("apache"), "--apache-compatible");

        await Assert.That(dates.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(tool.PathOf("dates/shop/Order.g.cs"))).Contains("public global::System.DateTime Day { get; set; }");
        await Assert.That(apache.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(tool.PathOf("apache/shop/Order.g.cs"))).Contains("global::Avro.Specific.ISpecificRecord");
        await Assert.That(File.Exists(tool.PathOf("apache/AvroSharp/Generated/ApacheDecimals.g.cs"))).IsTrue();
    }

    [Test]
    public async Task Gen_NestedNamespaces_BecomeNestedFolders_OrOneFolderWithFlat()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("event.avsc", """
            {"type":"record","name":"Event","namespace":"com.example.events","fields":[
              {"name":"kind","type":{"type":"enum","name":"Kind","namespace":"com.example.common","symbols":["A"]}},
              {"name":"top","type":{"type":"fixed","name":"Top","namespace":"","size":2}}]}
            """);

        var nested = ToolRunner.Run("gen", schema, "-o", tool.PathOf("nested"));
        var flat = ToolRunner.Run("gen", schema, "-o", tool.PathOf("flat"), "--flat");

        await Assert.That(nested.ExitCode).IsEqualTo(0);
        await Assert.That(Files(tool.PathOf("nested"))).IsEquivalentTo(new[] { "Top.g.cs", "com/example/common/Kind.g.cs", "com/example/events/Event.g.cs" }, CollectionOrdering.Any);
        await Assert.That(flat.ExitCode).IsEqualTo(0);
        await Assert.That(Files(tool.PathOf("flat"))).IsEquivalentTo(new[] { "Top.g.cs", "com.example.common.Kind.g.cs", "com.example.events.Event.g.cs" }, CollectionOrdering.Any);
    }

    [Test]
    public async Task Gen_NamespaceMap_MapsNamespaces_AndTheNamespacesUnderThem()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("event.avsc", """
            {"type":"record","name":"Event","namespace":"com.example.events","fields":[
              {"name":"kind","type":{"type":"enum","name":"Kind","namespace":"com.example.common","symbols":["A"]}},
              {"name":"other","type":{"type":"fixed","name":"Other","namespace":"org.other","size":2}}]}
            """);
        var output = tool.PathOf("out");

        // com.example maps the namespaces under it; the longer com.example.common wins for Kind; org.other is unmapped.
        var result = ToolRunner.Run(
            "gen", schema, "-o", output, "--namespace-map", "com.example:Example", "-m", "com.example.common:Shared.Types");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Files(output)).IsEquivalentTo(new[] { "Example/events/Event.g.cs", "Shared/Types/Kind.g.cs", "org/other/Other.g.cs" }, CollectionOrdering.Any);
        var code = File.ReadAllText(tool.PathOf("out/Example/events/Event.g.cs"));
        await Assert.That(code).Contains("namespace Example.events");
        await Assert.That(code).Contains("public global::Shared.Types.Kind Kind { get; set; }");

        // Only the C# namespace changes: the schema keeps its Avro names.
        await Assert.That(code).Contains("com.example.events.Event");
    }

    [Test]
    [Arguments(new[] { "--namespace", "com.example:Example" }, "to map namespaces as avrogen's --namespace does, use --namespace-map")]
    [Arguments(new[] { "--namespace-map", "com.example:Example", "--apache-compatible" }, "--namespace-map cannot be combined with --apache-compatible")]
    [Arguments(new[] { "--namespace-map", "com.example" }, "--namespace-map 'com.example' is not avro.namespace:CSharp.Namespace")]
    [Arguments(new[] { "--namespace-map", "com.example:" }, "--namespace-map 'com.example:' is not avro.namespace:CSharp.Namespace")]
    [Arguments(new[] { "--namespace-map", "com..example:Example" }, "--namespace-map 'com..example:Example' is not avro.namespace:CSharp.Namespace")]
    [Arguments(new[] { "--namespace-map", "a:B", "-m", "a:C" }, "--namespace-map maps 'a' more than once.")]
    [Arguments(new[] { "--namespace", "My Models" }, "--namespace 'My Models' is not a C# namespace")]
    [Arguments(new[] { "--namespace", "1abc" }, "--namespace '1abc' is not a C# namespace")]
    public async Task Gen_InvalidNamespaceOptions_AreUsageErrors(string[] options, string message)
    {
        var result = ToolRunner.Run(["gen", "a.avsc", "-o", "out", .. options]);

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Error).Contains(message);
    }

    [Test]
    public async Task Gen_ReportsRenamedProperties_AsInfo()
    {
        using var tool = new ToolRunner();
        var file = tool.Write("clash.avsc", """{"type":"record","name":"Clash","fields":[{"name":"USER_ID","type":"long"},{"name":"userId","type":"long"}]}""");

        var result = ToolRunner.Run("gen", file, "-o", tool.PathOf("out"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains($"{file}: info AVROGEN005: Field 'Clash.userId' is property UserId_");
    }

    [Test]
    public async Task Gen_InvalidSchema_ReportsEveryBadFile_AndWritesNothing()
    {
        using var tool = new ToolRunner();
        tool.Write("s/good.avsc", Status);
        var bad = tool.Write("s/bad.avsc", """{"type":"record","name":"Bad","fields":[{"name":"x","type":"nope"}]}""");
        var broken = tool.Write("s/broken.avsc", """{"type":"record",""");
        var output = tool.PathOf("out");

        var result = ToolRunner.Run("gen", tool.PathOf("s"), "-o", output);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains($"{bad}(1,60): error AVROGEN001: 'nope' is not a defined type.");
        await Assert.That(result.Error).Contains($"{broken}(1,");
        await Assert.That(result.Error).Contains("No files written: 2 of 3 schema file(s) are not valid.");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task Gen_DifferentDefinitionsOfOneType_NameTheOtherFile()
    {
        using var tool = new ToolRunner();
        var first = tool.Write("a.avsc", Status);
        var second = tool.Write("b.avsc", """{"type":"enum","name":"Status","namespace":"shop","symbols":["OTHER"]}""");

        var result = ToolRunner.Run("gen", first, second, "-o", tool.PathOf("out"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains($"{second}(");
        await Assert.That(result.Error).Contains($"It is also defined in {first}.");
    }

    [Test]
    public async Task Gen_GenerationFailure_IsReported_AndWritesNothing()
    {
        using var tool = new ToolRunner();

        // Two union branches that both map to Guid: the generator cannot tell them apart.
        var file = tool.Write("u.avsc", """
            {"type":"record","name":"U","fields":[{"name":"id","type":[
              {"type":"string","logicalType":"uuid"},{"type":"fixed","name":"Id","size":16,"logicalType":"uuid"}]}]}
            """);

        var result = ToolRunner.Run("gen", file, "-o", tool.PathOf("out"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("error AVROGEN003:");
        await Assert.That(result.Error).Contains("No files written.");
        await Assert.That(Directory.Exists(tool.PathOf("out"))).IsFalse();
    }

    [Test]
    public async Task Gen_MissingInputs_AndEmptyFolders_AreReported()
    {
        using var tool = new ToolRunner();
        var good = tool.Write("good.avsc", Status);
        Directory.CreateDirectory(tool.PathOf("empty"));
        var missing = tool.PathOf("missing.avsc");

        var result = ToolRunner.Run("gen", good, missing, tool.PathOf("empty"), "-o", tool.PathOf("out"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains($"error: '{missing}' is not a file or folder.");
        await Assert.That(result.Error).Contains($"error: No .avsc files in '{tool.PathOf("empty")}'.");
        await Assert.That(Directory.Exists(tool.PathOf("out"))).IsFalse();
    }

    /// <summary>The files under a folder, as paths relative to it with '/' separators, in order.</summary>
    private static string[] Files(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(System.StringComparer.Ordinal)
            .ToArray();

    [Test]
    public async Task Gen_OutputThatIsAFile_IsReported()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("s.avsc", Status);
        var notAFolder = tool.Write("out", "a file, not a folder");

        var result = ToolRunner.Run("gen", schema, "-o", notAFolder);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains($"error: Cannot write to '{notAFolder}':");
    }

    /// <summary>
    /// Names that differ only by case became one file on Windows and macOS, silently keeping the last type (#131).
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Gen_TypesWhoseFilesDifferOnlyByCase_AreReported_AndWriteNothing(bool flat)
    {
        using var tool = new ToolRunner();
        var upper = tool.Write("upper.avsc", """{"type":"record","name":"Order","namespace":"cs","fields":[{"name":"x","type":"int"}]}""");
        var lower = tool.Write("lower.avsc", """{"type":"record","name":"order","namespace":"cs","fields":[{"name":"x","type":"int"}]}""");
        var output = tool.PathOf("out");

        var result = ToolRunner.Run(["gen", upper, lower, "-o", output, .. flat ? new[] { "--flat" } : []]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("error AVROGEN003: The types 'cs.Order' and 'cs.order' would both be written to");
        await Assert.That(result.Error).Contains(": their names differ only by case, which Windows and macOS file names don't tell apart.");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task Gen_ANamespaceMapThatMergesTwoTypesOfOneName_IsReported()
    {
        using var tool = new ToolRunner();
        var a = tool.Write("a.avsc", """{"type":"record","name":"X","namespace":"a","fields":[{"name":"x","type":"int"}]}""");
        var b = tool.Write("b.avsc", """{"type":"record","name":"X","namespace":"b","fields":[{"name":"x","type":"int"}]}""");

        var result = ToolRunner.Run("gen", a, b, "-o", tool.PathOf("out"), "-m", "a:M", "-m", "b:M");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("error AVROGEN003: The types 'a.X' and 'b.X' are both the C# type M.X.");
        await Assert.That(Directory.Exists(tool.PathOf("out"))).IsFalse();
    }

    /// <summary>Two files that need each other's types are parsed together (#16).</summary>
    [Test]
    public async Task Gen_FilesThatNeedEachOthersTypes_Generate()
    {
        using var tool = new ToolRunner();
        var a = tool.Write("a.avsc", """{"type":"record","name":"A","namespace":"x.y","fields":[{"name":"b","type":["null","x.z.B"]}]}""");
        var b = tool.Write("b.avsc", """{"type":"record","name":"B","namespace":"x.z","fields":[{"name":"a","type":["null","x.y.A"]}]}""");

        var result = ToolRunner.Run("gen", a, b, "-o", tool.PathOf("out"), "--flat");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(tool.PathOf("out/x.y.A.g.cs"))).IsTrue();
        await Assert.That(File.Exists(tool.PathOf("out/x.z.B.g.cs"))).IsTrue();
    }

    /// <summary>When files that need each other's types still fail together, the error names the cycle.</summary>
    [Test]
    public async Task Gen_FilesThatNeedEachOthersTypes_WithAnotherError_NameTheCycle()
    {
        using var tool = new ToolRunner();
        var a = tool.Write("a.avsc", """{"type":"record","name":"A","namespace":"x.y","fields":[{"name":"b","type":["null","x.z.B"]}]}""");
        var b = tool.Write("b.avsc", """{"type":"record","name":"B","namespace":"x.z","fields":[{"name":"a","type":["null","x.y.A"]},{"name":"c","type":"x.z.Missing"}]}""");

        var result = ToolRunner.Run("gen", a, b, "-o", tool.PathOf("out"));

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Error).Contains("'x.z.B' is not a defined type.");
        await Assert.That(result.Error).Contains($"It is defined in {b}, which itself needs 'x.y.A' from this file. Files that need each other's types are parsed together, but these two have another error between them: check both.");
    }

    /// <summary>--required-members and --type-kind record (#15).</summary>
    [Test]
    public async Task Gen_RequiredMembersAndRecords()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("order.avsc", """{"type":"record","name":"Order","namespace":"shop","fields":[{"name":"id","type":"long"},{"name":"note","type":"string","default":""}]}""");

        var result = ToolRunner.Run("gen", schema, "-o", tool.PathOf("out"), "--required-members", "--type-kind", "record");
        var tooOld = ToolRunner.Run("gen", schema, "-o", tool.PathOf("old"), "--type-kind", "record", "--language-version", "8");
        var requiredTooOld = ToolRunner.Run("gen", schema, "-o", tool.PathOf("old2"), "--required-members", "--language-version", "10");

        var code = File.ReadAllText(tool.PathOf("out/shop/Order.g.cs"));
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(code).Contains("public sealed partial record class Order");
        await Assert.That(code).Contains("public required long Id { get; set; }");
        await Assert.That(code).Contains("public string Note { get; set; }");
        await Assert.That(tooOld.ExitCode).IsEqualTo(2);
        await Assert.That(tooOld.Error).Contains("--type-kind record needs --language-version 9 or later.");
        await Assert.That(requiredTooOld.ExitCode).IsEqualTo(2);
        await Assert.That(requiredTooOld.Error).Contains("--required-members needs --language-version 11 or later.");
    }

    /// <summary>--accessibility internal and --unsealed (#18).</summary>
    [Test]
    public async Task Gen_InternalAndUnsealedTypes()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("order.avsc", """{"type":"record","name":"Order","namespace":"shop","fields":[{"name":"status","type":{"type":"enum","name":"Status","symbols":["NEW"]}}]}""");

        var result = ToolRunner.Run("gen", schema, "-o", tool.PathOf("out"), "--accessibility", "internal", "--unsealed");
        var wrong = ToolRunner.Run("gen", schema, "-o", tool.PathOf("other"), "--accessibility", "private");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(tool.PathOf("out/shop/Order.g.cs"))).Contains("internal partial class Order");
        await Assert.That(File.ReadAllText(tool.PathOf("out/shop/Status.g.cs"))).Contains("internal enum Status");
        await Assert.That(wrong.ExitCode).IsEqualTo(2);
    }

    /// <summary>--collection-type (#18), and its conflict with --apache-compatible.</summary>
    [Test]
    public async Task Gen_CollectionType()
    {
        using var tool = new ToolRunner();
        var schema = tool.Write("bag.avsc", """{"type":"record","name":"Bag","namespace":"coll","fields":[{"name":"xs","type":{"type":"array","items":"long"}}]}""");

        var result = ToolRunner.Run("gen", schema, "-o", tool.PathOf("out"), "--collection-type", "array");
        var apache = ToolRunner.Run("gen", schema, "-o", tool.PathOf("apache"), "--collection-type", "readonlylist", "--apache-compatible");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(File.ReadAllText(tool.PathOf("out/coll/Bag.g.cs"))).Contains("public long[] Xs { get; set; }");
        await Assert.That(apache.ExitCode).IsEqualTo(2);
        await Assert.That(apache.Error).Contains("cannot be combined with --apache-compatible");
    }
}
