using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// A generated type named like a namespace or type the generated code refers to, or a prefix of one, hid it, and the
/// code didn't compile (#161): a namespace map onto AvroSharp with a record Serialization, or onto System with a record
/// Collections. The generator checks the names against the list of names its code uses, which these tests keep complete.
/// </summary>
public class ExternalNameTests
{
    private const string Everything = """
        {"type":"record","name":"All","namespace":"names","fields":[
          {"name":"s","type":"string"},{"name":"b","type":"bytes"},{"name":"i","type":"int","default":1},
          {"name":"note","type":["null","string"],"default":null},{"name":"any","type":["null","int","string"]},
          {"name":"list","type":{"type":"array","items":"long"},"default":[1]},
          {"name":"map","type":{"type":"map","values":"string"},"default":{"a":"b"}},
          {"name":"day","type":{"type":"int","logicalType":"date"},"default":1},
          {"name":"at","type":{"type":"long","logicalType":"timestamp-millis"}},
          {"name":"micros","type":{"type":"long","logicalType":"timestamp-micros"}},
          {"name":"local","type":{"type":"long","logicalType":"local-timestamp-millis"}},
          {"name":"time","type":{"type":"int","logicalType":"time-millis"}},
          {"name":"id","type":{"type":"string","logicalType":"uuid"}},
          {"name":"money","type":{"type":"bytes","logicalType":"decimal","precision":10,"scale":2},"default":"\u0001"},
          {"name":"cents","type":{"type":"fixed","name":"Cents","size":8,"logicalType":"decimal","precision":12,"scale":2}},
          {"name":"hash","type":{"type":"fixed","name":"Hash","size":2},"default":"ab"},
          {"name":"kind","type":{"type":"enum","name":"Kind","symbols":["A","B"],"default":"A"},"default":"B"},
          {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"x","type":"int"}]},"default":{"x":1}},
          {"name":"next","type":["null","All"],"default":null}
        ]}
        """;

    private static readonly Regex s_reference = new(@"global::(?<name>(System|AvroSharp|Avro)(\.[A-Za-z_][A-Za-z0-9_]*)+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    [Test]
    public async Task TheGeneratedCode_RefersOnlyToTheNamesTheGeneratorChecks()
    {
        var external = (string[])typeof(CSharpCodeGenerator).GetField("ExternalNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var schema = AvroSchema.Parse(Everything);
        var unlisted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var options in Options())
        {
            foreach (var source in CSharpCodeGenerator.Generate([schema], options))
            {
                foreach (Match match in s_reference.Matches(source.Text))
                {
                    var name = match.Groups["name"].Value;
                    if (!external.Any(e => string.Equals(e, name, StringComparison.Ordinal) || name.StartsWith(e + ".", StringComparison.Ordinal)))
                    {
                        unlisted.Add(name);
                    }
                }
            }
        }

        await Assert.That(unlisted).IsEmpty();
    }

    [Test]
    [Arguments("AvroSharp", "Serialization", "AvroSharp.Serialization.AvroLogicalValues")]
    [Arguments("System", "Collections", "System.Collections.Generic.Dictionary")]
    [Arguments("System", "DateOnly", "System.DateOnly")]
    [Arguments("AvroSharp.Serialization", "Generated", "AvroSharp.Serialization.Generated.AvroBooleanSerializer")]
    public async Task ATypeThatHidesANameTheCodeUses_IsAnError(string target, string record, string hidden)
    {
        var schema = AvroSchema.Parse($$"""{"type":"record","name":"{{record}}","namespace":"com.acme","fields":[{"name":"x","type":"int"}]}""");
        var options = new CodeGenOptions { NamespaceMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["com.acme"] = target } };

        var ex = Assert.Throws<InvalidOperationException>(() => CSharpCodeGenerator.Generate([schema], options));

        await Assert.That(ex.Message).IsEqualTo($"The type 'com.acme.{record}' is the C# type {target}.{record}, which would hide {hidden}, which the generated code uses. Map its namespace to another C# namespace.");
    }

    /// <summary>Only names that hide one: a type in AvroSharp's or System's namespace that doesn't is generated.</summary>
    [Test]
    public async Task ATypeInTheSameNamespace_ThatHidesNothing_IsGenerated()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"Order","namespace":"com.acme","fields":[{"name":"x","type":"int"}]}""");
        var options = new CodeGenOptions { NamespaceMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["com.acme"] = "AvroSharp" } };

        var sources = CSharpCodeGenerator.Generate([schema], options);

        await Assert.That(sources.Single().Namespace).IsEqualTo("AvroSharp");
    }

    private static IEnumerable<CodeGenOptions> Options()
    {
        foreach (var raw in new[] { false, true })
        {
            foreach (var apache in new[] { false, true })
            {
                foreach (var (language, nullable) in new[] { (7, false), (11, true), (14, true) })
                {
                    foreach (var dateOnly in new[] { false, true })
                    {
                        yield return new CodeGenOptions
                        {
                            LogicalTypes = raw ? LogicalTypeMapping.Raw : LogicalTypeMapping.Native,
                            ApacheCompatible = apache,
                            LanguageVersion = language,
                            NullableAnnotations = nullable,
                            TargetHasDateOnly = dateOnly,
                        };
                    }
                }
            }
        }
    }
}
