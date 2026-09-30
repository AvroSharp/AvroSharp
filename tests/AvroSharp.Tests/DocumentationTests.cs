using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AvroSharp.Tests;

/// <summary>Documentation whose code must be the code of a sample that CI builds and runs.</summary>
public class DocumentationTests
{
    /// <summary>
    /// Every C# block of a guide appears in its sample: the migration guide in the Migration sample (#21), and each
    /// getting-started page in its own. CI builds and runs the samples, so a block that drifts from its sample no longer
    /// compiles, or no longer does what the page says.
    /// </summary>
    [Test]
    [Arguments("docs/migrating-from-apache-avro.md", "samples/Migration/Program.cs", 10)]
    [Arguments("docs/getting-started/index.md", "samples/GettingStarted/Program.cs", 4)]
    [Arguments("docs/getting-started/generated-types.md", "samples/GeneratorPackage/Program.cs", 3)]
    [Arguments("docs/getting-started/containers.md", "samples/ContainerFiles/Program.cs", 3)]
    [Arguments("docs/getting-started/schema-evolution.md", "samples/SchemaEvolution/Program.cs", 3)]
    [Arguments("docs/getting-started/logical-types.md", "samples/LogicalTypes/Program.cs", 3)]
    [Arguments("docs/getting-started/json.md", "samples/Json/Program.cs", 3)]
    public async Task EveryCSharpBlock_IsCodeOfItsSample(string guide, string sample, int minimumBlocks)
    {
        var root = TestConventionTests.RepositoryRoot();
        var program = Lines(File.ReadAllText(Path.Combine(root, sample)));
        var blocks = CSharpBlocks(File.ReadAllText(Path.Combine(root, guide))).ToList();

        var missing = blocks.Where(block => !Contains(program, Lines(block))).Select(block => Lines(block)[0]).ToList();

        await Assert.That(blocks.Count).IsGreaterThanOrEqualTo(minimumBlocks);
        await Assert.That(missing).IsEmpty();
    }

    private static IEnumerable<string> CSharpBlocks(string markdown)
    {
        const string Open = "```csharp\n";
        var text = string.Join("\n", markdown.Split('\n').Select(line => line.TrimEnd('\r')));
        for (var start = text.IndexOf(Open, StringComparison.Ordinal); start >= 0; start = text.IndexOf(Open, start, StringComparison.Ordinal))
        {
            start += Open.Length;
            var end = text.IndexOf("```", start, StringComparison.Ordinal);
            yield return text[start..end];
            start = end;
        }
    }

    // Lines without their leading and trailing space, and without blank lines, so indentation and line endings don't matter.
    private static List<string> Lines(string code) =>
        code.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    // Whether block appears in program as consecutive lines.
    private static bool Contains(List<string> program, List<string> block)
    {
        for (var i = 0; i + block.Count <= program.Count; i++)
        {
            if (block.Select((line, k) => string.Equals(line, program[i + k], StringComparison.Ordinal)).All(same => same))
            {
                return true;
            }
        }

        return false;
    }
}
