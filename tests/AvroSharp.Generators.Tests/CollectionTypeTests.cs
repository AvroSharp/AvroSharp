using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>AvroSharpCollectionType (#18): arrays as List&lt;T&gt;, T[] or IReadOnlyList&lt;T&gt;, through every array path.</summary>
public class CollectionTypeTests
{
    // Every way the generated code reads and writes an array.
    private const string Schema = """
        {"type":"record","name":"Bag","namespace":"coll","fields":[
          {"name":"longs","type":{"type":"array","items":"long"}},
          {"name":"ints","type":{"type":"array","items":"int"},"default":[1,2]},
          {"name":"flags","type":{"type":"array","items":"boolean"}},
          {"name":"floats","type":{"type":"array","items":"float"}},
          {"name":"doubles","type":{"type":"array","items":"double"},"default":[]},
          {"name":"names","type":{"type":"array","items":"string"}},
          {"name":"lines","type":{"type":"array","items":{"type":"record","name":"Line","fields":[{"name":"qty","type":"int"}]}}},
          {"name":"amounts","type":{"type":"array","items":{"type":"bytes","logicalType":"decimal","precision":9,"scale":2}}},
          {"name":"kinds","type":{"type":"array","items":{"type":"enum","name":"Kind","symbols":["A","B"]}}},
          {"name":"grid","type":{"type":"array","items":{"type":"array","items":"int"}}},
          {"name":"byName","type":{"type":"map","values":{"type":"array","items":"string"}}},
          {"name":"maybe","type":["null",{"type":"array","items":"long"}],"default":null}
        ]}
        """;

    // An older version whose longs were ints: they are read as longs, the promoted array path.
    private static readonly string s_olderSchema = Schema.Replace(
        """{"name":"longs","type":{"type":"array","items":"long"}}""", """{"name":"longs","type":{"type":"array","items":"int"}}""", StringComparison.Ordinal);

    private static readonly string[] s_net10 =
        ["NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"];

    private static readonly string[] s_netStandard = ["NETSTANDARD", "NETSTANDARD2_0"];

    public static IEnumerable<CodeGenCollectionType> Kinds() => [CodeGenCollectionType.List, CodeGenCollectionType.Array, CodeGenCollectionType.ReadOnlyList];

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task EveryArrayPath_RoundTrips(CodeGenCollectionType kind)
    {
        var schema = AvroSchema.Parse(Schema);
        var assembly = Load(kind, s_net10);
        var bag = assembly.GetType("coll.Bag")!;
        var use = assembly.GetType("Use")!;
        var writer = GenericDatumWriter.Create(schema);

        foreach (var value in AvroRandomData.Generate(schema, seed: 18).Take(50))
        {
            var bytes = writer.WriteToArray(value);
            await Assert.That((byte[])use.GetMethod("RoundTrip")!.Invoke(null, [bytes])!).IsEquivalentTo(bytes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }

        // new Bag() takes the defaults: ints [1, 2], doubles [].
        var created = (byte[])use.GetMethod("Created")!.Invoke(null, null)!;
        var defaults = GenericDatumReader.Create(schema).Read(created).AsRecord();
        await Assert.That(defaults["ints"].AsArray().Select(v => v.AsInt32()).ToList()).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(bag.GetProperty("Longs")!.PropertyType.Name).IsEqualTo(kind switch
        {
            CodeGenCollectionType.Array => "Int64[]",
            CodeGenCollectionType.ReadOnlyList => "IReadOnlyList`1",
            _ => "List`1",
        });
    }

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task PromotedArrays_FromAnOlderVersion_AreRead(CodeGenCollectionType kind)
    {
        var use = Load(kind, s_net10).GetType("Use")!;
        var older = AvroSchema.Parse(s_olderSchema);
        var olderValue = new AvroRandomData(3).Next(older).AsRecord();
        olderValue["longs"] = AvroValue.FromArray([1, 2, 3]);
        var bytes = GenericDatumWriter.Create(older).WriteToArray(olderValue);

        var longs = (IEnumerable<long>)use.GetMethod("Promoted")!.Invoke(null, [bytes, older])!;

        await Assert.That(longs.ToList()).IsEquivalentTo([1L, 2L, 3L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task TheCode_CompilesForNetStandard(CodeGenCollectionType kind)
    {
        var sources = CSharpCodeGenerator.Generate([AvroSchema.Parse(Schema)], new CodeGenOptions { CollectionType = kind, NullableAnnotations = false, LanguageVersion = 7 });

        var diagnostics = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.CSharp7_3, s_netStandard, [.. sources.Select(s => s.Text)]).GetDiagnostics();

        await Assert.That(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public async Task ArraysOtherThanLists_AreNotForTheApacheMode()
    {
        var ex = Assert.Throws<ArgumentException>(() => CSharpCodeGenerator.Generate([AvroSchema.Parse(Schema)], new CodeGenOptions { CollectionType = CodeGenCollectionType.Array, ApacheCompatible = true }));

        await Assert.That(ex.Message).Contains("List<T>");
    }

    [Test]
    public async Task TheMSBuildProperty_AndFileMetadata_SetIt()
    {
        var (sources, diagnostics, compile) = GeneratorHarness.RunWithProperties(
            [("bag.avsc", Schema), ("other.avsc", """{"type":"record","name":"Other","namespace":"coll","fields":[{"name":"xs","type":{"type":"array","items":"int"}}]}""")],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpCollectionType"] = "readonlylist" },
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["other.avsc"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["AvroSharpCollectionType"] = "array" },
            },
            s_net10);

        await Assert.That(diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(compile.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Single(s => s.HintName.EndsWith("Bag.g.cs", StringComparison.Ordinal)).SourceText.ToString()).Contains("global::System.Collections.Generic.IReadOnlyList<long> Longs");
        await Assert.That(sources.Single(s => s.HintName.EndsWith("Other.g.cs", StringComparison.Ordinal)).SourceText.ToString()).Contains("public int[] Xs");
    }

    private static System.Reflection.Assembly Load(CodeGenCollectionType kind, string[] symbols)
    {
        var sources = CSharpCodeGenerator.Generate([AvroSchema.Parse(Schema)], new CodeGenOptions { CollectionType = kind });
        const string Use = """
            public static class Use
            {
                public static byte[] RoundTrip(byte[] bytes) => coll.Bag.FromAvroBytes(bytes).ToAvroBytes();

                public static byte[] Created() => new coll.Bag().ToAvroBytes();

                public static System.Collections.Generic.IEnumerable<long> Promoted(byte[] bytes, AvroSharp.Schemas.AvroSchema writer) =>
                    coll.Bag.FromAvroBytes(bytes, writer).Longs;
            }
            """;
        var compilation = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.Latest, symbols, [.. sources.Select(s => s.Text), Use]);
        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success || emitted.Diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Warning))
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning)));
        }

        return System.Reflection.Assembly.Load(image.ToArray());
    }
}
