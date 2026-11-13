using System;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>The partial-method hooks of generated records (#119): OnCreated, OnSerializing and OnDeserialized.</summary>
public class HookTests
{
    private const string Schema = """
        {"type":"record","name":"Order","namespace":"shop","fields":[
          {"name":"id","type":"long"},{"name":"status","type":"string","default":"new"}]}
        """;

    // The user's part of the type: it counts the calls, and normalizes a field as it is read.
    private const string UserPart = """
        namespace shop
        {
            public sealed partial class Order
            {
                public static int Created, Serializing, Deserialized;

                partial void OnCreated() { Created++; Status = Status.ToUpperInvariant(); }

                partial void OnSerializing() => Serializing++;

                partial void OnDeserialized() { Deserialized++; Status = Status.Trim(); }
            }
        }

        public static class Use
        {
            public static string Run()
            {
                var order = new shop.Order { Id = 1 };                      // OnCreated: Status "NEW"
                order.Status = "  paid  ";
                var bytes = order.ToAvroBytes();                            // OnSerializing
                var read = shop.Order.FromAvroBytes(bytes);                 // OnDeserialized: Status "paid"
                var json = shop.Order.FromAvroJson(order.ToAvroJson());     // OnSerializing, OnDeserialized
                var older = AvroSharp.Schemas.AvroSchema.Parse("{\"type\":\"record\",\"name\":\"Order\",\"namespace\":\"shop\",\"fields\":[{\"name\":\"id\",\"type\":\"long\"}]}");
                var resolved = shop.Order.FromAvroBytes(new byte[] { 4 }, older);  // OnDeserialized, through the plan
                return string.Join(",", shop.Order.Created, shop.Order.Serializing, shop.Order.Deserialized, read.Status, json.Status, resolved.Status);
            }
        }
        """;

    [Test]
    public async Task Hooks_RunAtCreation_BeforeWriting_AndAfterEveryRead()
    {
        var sources = CSharpCodeGenerator.Generate([AvroSchema.Parse(Schema)]).Select(s => s.Text).Append(UserPart).ToArray();
        var compilation = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.Latest, ["NET8_0_OR_GREATER", "NET5_0_OR_GREATER"], sources);
        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image);
        await Assert.That(emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();

        var result = (string)System.Reflection.Assembly.Load(image.ToArray()).GetType("Use")!.GetMethod("Run")!.Invoke(null, null)!;

        // Created once by the user (readers don't run OnCreated), serialized twice, deserialized three times; the
        // resolved read gets the default "new", trimmed.
        await Assert.That(result).IsEqualTo("1,2,3,paid,paid,new");
    }

    [Test]
    public async Task Hooks_ThatAreNotImplemented_CompileToNothing_AtCSharp7()
    {
        var sources = CSharpCodeGenerator.Generate([AvroSchema.Parse(Schema)], new CodeGenOptions { LanguageVersion = 7, NullableAnnotations = false }).Select(s => s.Text).ToArray();

        var diagnostics = GeneratorHarness.CreateCompilation(true, false, LanguageVersion.CSharp7_3, sources).GetDiagnostics();

        await Assert.That(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(string.Join("\n", sources)).Contains("partial void OnDeserialized();");
    }
}
