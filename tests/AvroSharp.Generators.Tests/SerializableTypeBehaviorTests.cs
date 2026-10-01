using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// The attribute-driven generator's behavior (#31): unions, recursion, naming, inheritance, record classes, the
/// AvroTypes registration, schema resolution, and incremental runs.
/// </summary>
public class SerializableTypeBehaviorTests
{
    private const string Usings = "using System; using System.Collections.Generic; using AvroSharp.Serialization; using AvroSharp.Schemas; namespace B; ";

    [Test]
    public async Task AnObjectUnion_WritesEachBranchByItsRuntimeType()
    {
        const string Code = Usings + """
            [AvroSerializable] public partial class Card { public string Number { get; set; } = ""; }
            [AvroSerializable] public partial class Cash { public int Cents { get; set; } }
            [AvroSerializable] public partial class Payment { [AvroUnion(typeof(Card), typeof(Cash))] public object? Method { get; set; } }
            public static class Probe
            {
                public static string Run()
                {
                    var card = Payment.FromAvroBytes(new Payment { Method = new Card { Number = "4111" } }.ToAvroBytes());
                    var cash = Payment.FromAvroBytes(new Payment { Method = new Cash { Cents = 250 } }.ToAvroBytes());
                    var none = Payment.FromAvroBytes(new Payment().ToAvroBytes());
                    return ((Card)card.Method!).Number + "|" + ((Cash)cash.Method!).Cents + "|" + (none.Method is null) + "|" + Payment.Schema.CanonicalForm;
                }
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("4111|250|True|" + """{"name":"B.Payment","type":"record","fields":[{"name":"Method","type":["null",{"name":"B.Card","type":"record","fields":[{"name":"Number","type":"string"}]},{"name":"B.Cash","type":"record","fields":[{"name":"Cents","type":"int"}]}]}]}""");
    }

    [Test]
    public async Task ARecursiveType_RefersToItselfByName()
    {
        const string Code = Usings + """
            [AvroSerializable] public partial class Node { public int Value { get; set; } public Node? Next { get; set; } }
            public static class Probe
            {
                public static string Run()
                {
                    var list = Node.FromAvroBytes(new Node { Value = 1, Next = new Node { Value = 2 } }.ToAvroBytes());
                    return list.Value + "," + list.Next!.Value + "," + (list.Next.Next is null) + "|" + Node.Schema.CanonicalForm;
                }
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("1,2,True|" + """{"name":"B.Node","type":"record","fields":[{"name":"Value","type":"int"},{"name":"Next","type":["null","B.Node"]}]}""");
    }

    [Test]
    public async Task TheAssemblyNamingPolicy_AppliesUnlessTheTypeSetsItsOwn()
    {
        const string Code = "using System.Linq; using AvroSharp.Serialization; using AvroSharp.Schemas; [assembly: AvroNamingPolicy(AvroNaming.CamelCase)] namespace B; " + """
            [AvroSerializable] public partial class A { public int OrderId { get; set; } public string URLValue { get; set; } = ""; }
            [AvroSerializable(FieldNames = AvroNaming.AsWritten)] public partial class B2 { public int OrderId { get; set; } }
            public static class Probe
            {
                public static string Run() => string.Join(",", ((RecordSchema)A.Schema).Fields.Select(f => f.Name)) + "|" + ((RecordSchema)B2.Schema).Fields[0].Name;
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("orderId,urlValue|OrderId");
    }

    [Test]
    public async Task InheritedMembers_ComeFirst_AndARecordClassWorks()
    {
        const string Code = Usings + """
            public record Entity { public long Id { get; set; } }
            [AvroSerializable] public partial record Customer : Entity { public string Name { get; set; } = ""; }
            public static class Probe
            {
                public static string Run()
                {
                    var c = Customer.FromAvroBytes(new Customer { Id = 7, Name = "Ada" }.ToAvroBytes());
                    return c.Id + "," + c.Name + "," + (c == new Customer { Id = 7, Name = "Ada" }) + "|" + Customer.Schema.CanonicalForm;
                }
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("7,Ada,True|" + """{"name":"B.Customer","type":"record","fields":[{"name":"Id","type":"long"},{"name":"Name","type":"string"}]}""");
    }

    [Test]
    public async Task DefaultsAndEnumDefaults_AreInTheSchema_AndResolveOlderData()
    {
        const string Code = Usings + """
            public enum Tier { [AvroEnumDefault] Unknown, Gold }
            [AvroSerializable(Name = "Member")] public partial class V2
            {
                public string Name { get; set; } = "";
                [AvroDefault("5")] public int Visits { get; set; }
                public Tier Tier { get; set; }
            }
            public static class Probe
            {
                public static string Run()
                {
                    // Version 1 had only a name; and a symbol Tier doesn't know.
                    var v1 = AvroSchema.Parse("{\"type\":\"record\",\"name\":\"B.Member\",\"fields\":[{\"name\":\"Name\",\"type\":\"string\"},{\"name\":\"Tier\",\"type\":{\"type\":\"enum\",\"name\":\"B.Tier\",\"symbols\":[\"Platinum\"]}}]}");
                    var data = AvroSharp.Generic.GenericDatumWriter.Create(v1).WriteToArray(new AvroSharp.Generic.GenericRecord((RecordSchema)v1) { ["Name"] = "Grace", ["Tier"] = AvroSharp.Generic.AvroValue.FromEnum((EnumSchema)((RecordSchema)v1).GetField("Tier").Schema, "Platinum") });
                    var read = V2.FromAvroBytes(data, v1);
                    return read.Name + "," + read.Visits + "," + read.Tier;
                }
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("Grace,5,Unknown");
    }

    [Test]
    public async Task GeneratedTypes_AreRegisteredInAvroTypes()
    {
        const string Code = Usings + """
            [AvroSerializable] public partial class Ping { public int N { get; set; } }
            public static class Probe
            {
                public static string Run()
                {
                    var found = AvroTypes.TryGet(typeof(Ping), out var info);
                    var writer = new AvroSharp.IO.AvroWriter(new byte[16]);
                    info!.WriteObject(ref writer, new Ping { N = 21 });
                    var typed = AvroTypes.Get<Ping>();
                    var bytes = new Ping { N = 21 }.ToAvroBytes();
                    var reader = new AvroSharp.IO.AvroReader(bytes);
                    return found + "," + info.Schema.CanonicalForm + "," + typed.Read(ref reader).N + "," + AvroTypes.Get<int>().Schema.CanonicalForm;
                }
            }
            """;

        var result = Run(Code);

        await Assert.That(result).IsEqualTo("True," + """{"name":"B.Ping","type":"record","fields":[{"name":"N","type":"int"}]}""" + ",21,\"int\"");
    }

    [Test]
    public async Task ATypeInAnotherFile_Changing_RegeneratesTheTypeThatUsesIt()
    {
        const string Order = Usings + "[AvroSerializable] public partial class Order { public Line Line { get; set; } = new(); }";
        const string Line1 = Usings + "[AvroSerializable] public partial class Line { public int Quantity { get; set; } }";
        const string Line2 = Usings + "[AvroSerializable] public partial class Line { public int Quantity { get; set; } public string Sku { get; set; } = \"\"; }";

        var compilation = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.Latest, ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"], Order, Line1);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new SerializableTypeGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Latest));
        driver = driver.RunGenerators(compilation);
        var before = OrderSource(driver);
        var lineTree = compilation.SyntaxTrees.Last();
        var changed = compilation.ReplaceSyntaxTree(lineTree, CSharpSyntaxTree.ParseText(Line2, (CSharpParseOptions)lineTree.Options));
        driver = driver.RunGenerators(changed);

        await Assert.That(before).DoesNotContain("Sku");
        await Assert.That(OrderSource(driver)).Contains("Sku");
    }

    private static string OrderSource(GeneratorDriver driver) =>
        driver.GetRunResult().Results.Single().GeneratedSources.Single(s => s.HintName.StartsWith("B.Order", System.StringComparison.Ordinal)).SourceText.ToString();

    private static string Run(string code) =>
        (string)GeneratorHarness.LoadTypes(code).GetType("B.Probe")!.GetMethod("Run")!.Invoke(null, null)!;
}
