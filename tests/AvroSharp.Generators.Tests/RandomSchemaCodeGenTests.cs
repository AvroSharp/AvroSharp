using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AvroSharp.CodeGen;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;
using AvroSharp.Schemas;
using CsCheck;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// Random schemas through the code generator (#141): generate, compile, and round-trip values, as the runtime's
/// random-schema tests do for the generic model. The schemas have names chosen to break generated C# and random
/// defaults for every field type, the two gaps behind the #131 bugs. A failure shrinks to the smallest failing batch
/// and prints its schemas and the seed.
/// </summary>
/// <remarks>
/// <c>AVROSHARP_RANDOM_SCHEMA_BATCHES</c> (default 5, of 20 schemas each) and <c>AVROSHARP_RANDOM_SCHEMA_SEED</c> make
/// longer runs (the nightly fuzz workflow) and reruns of a reported failure.
/// </remarks>
public class RandomSchemaCodeGenTests
{
    private const int BatchSize = 20;

    // What a .NET 10 project defines, so the generated code's .NET-only branches are compiled and run too.
    private static readonly string[] s_net10Symbols =
        ["NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER"];

    // The errors code generation may report for a valid schema: a union whose branches map to one C# type, a type
    // that is also a namespace, and names the Apache.Avro compatibility mode cannot rename.
    private static readonly string[] s_documentedErrors =
    [
        "both map to the C# type", "which is also the namespace of", "cannot be generated in the Apache.Avro compatibility mode",
        "has a decimal that the generated C# decimal cannot hold",
    ];

    [Test]
    public async Task RandomSchemas_Generate_Compile_AndRoundTrip()
    {
        var batches = int.TryParse(Environment.GetEnvironmentVariable("AVROSHARP_RANDOM_SCHEMA_BATCHES"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 5;
        var seed = Environment.GetEnvironmentVariable("AVROSHARP_RANDOM_SCHEMA_SEED");
        var checkedSchemas = 0;

        // Each schema's types are in its own namespace, s0 to s19, so one compilation can hold a batch.
        var batch = RandomCodeGenSchemas.Json("s0").Array[BatchSize]
            .Select(schemas => schemas.Select((json, k) => json.Replace("\"s0.", $"\"s{k}.", StringComparison.Ordinal)).ToArray());

        batch.Sample(
            schemas =>
            {
                try
                {
                    System.Threading.Interlocked.Add(ref checkedSchemas, Check(schemas));
                }
                catch (Exception ex) when (SaveFailure(schemas, ex))
                {
                    throw;
                }
            },
            seed: seed,
            iter: batches,
            threads: 1,
            print: schemas => Environment.NewLine + string.Join(Environment.NewLine, schemas));

        await Assert.That(checkedSchemas).IsGreaterThan(0);
    }

    /// <summary>
    /// Writes a failing batch under AVROSHARP_RANDOM_SCHEMA_FAILURES, if it is set: a schema per file and the error. CsCheck
    /// only tries smaller batches after a failure, so the last one written is the one it reports. Returns false, so
    /// the exception goes on unchanged.
    /// </summary>
    private static bool SaveFailure(string[] schemas, Exception ex)
    {
        if (Environment.GetEnvironmentVariable("AVROSHARP_RANDOM_SCHEMA_FAILURES") is { Length: > 0 } root)
        {
            // The target frameworks run in parallel.
            var directory = Path.Combine(root, "net" + Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            for (var i = 0; i < schemas.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"schema{i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}.avsc"), schemas[i]);
            }

            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
        }

        return false;
    }

    /// <summary>Checks one batch; returns how many of its schemas were compiled and run.</summary>
    private static int Check(string[] batch)
    {
        var schemas = batch.Select(json => (Json: json, Schema: (RecordSchema)AvroSchema.Parse(json))).ToList();

        // Schemas the generator rejects with a documented error are left out; any other failure is a bug.
        var native = schemas.Where(s => Generates(s.Schema, apacheCompatible: false)).ToList();
        var apache = schemas.Where(s => Generates(s.Schema, apacheCompatible: true)).ToList();

        // One compilation per setting, with the source generator itself, as a project uses it.
        CompileWithoutWarnings(native.Select(s => s.Json), LanguageVersion.CSharp7_3, symbols: [], apacheCompatible: false);
        CompileWithoutWarnings(native.Select(s => s.Json), LanguageVersion.CSharp12, s_net10Symbols, apacheCompatible: false);
        CompileWithoutWarnings(apache.Select(s => s.Json), LanguageVersion.Latest, s_net10Symbols, apacheCompatible: true);
        var compilation = CompileWithoutWarnings(native.Select(s => s.Json), LanguageVersion.Latest, s_net10Symbols, apacheCompatible: false);

        var context = new AssemblyLoadContext("random-schemas", isCollectible: true);
        try
        {
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics));
            }

            image.Position = 0;
            var types = TypesBySchema(context.LoadFromStream(image));
            foreach (var (json, schema) in native)
            {
                try
                {
                    RoundTrip(types[schema.FullName], schema, json);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
                {
                    throw new InvalidOperationException($"{inner.GetType().Name}: {inner.Message} for {json}", inner);
                }
            }
        }
        finally
        {
            context.Unload();
        }

        return native.Count;
    }

    private static bool Generates(RecordSchema schema, bool apacheCompatible)
    {
        try
        {
            CSharpCodeGenerator.Generate([schema], new CodeGenOptions { ApacheCompatible = apacheCompatible });
            return true;
        }
        catch (Exception ex) when (ex is AvroException or InvalidOperationException && s_documentedErrors.Any(e => ex.Message.Contains(e, StringComparison.Ordinal)))
        {
            return false;
        }
    }

    private static CSharpCompilation CompileWithoutWarnings(IEnumerable<string> schemas, LanguageVersion version, string[] symbols, bool apacheCompatible)
    {
        var files = schemas.Select((json, i) => ($"schema{i}.avsc", json)).ToList();
        var compilation = GeneratorHarness.CreateCompilation(true, apacheCompatible, version, symbols, "internal static class Placeholder { }");
        GeneratorHarness.CreateDriver(files, apacheCompatible: apacheCompatible, languageVersion: version, preprocessorSymbols: symbols)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        // Renames are reported as information (AVROGEN005); anything else from the generator or the compiler fails.
        var problems = generatorDiagnostics.Where(d => d.Severity > DiagnosticSeverity.Info)
            .Concat(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning))
            .Select(d => d.ToString())
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"C# {version}{(apacheCompatible ? ", Apache.Avro compatibility mode" : string.Empty)}:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Take(20))}");
        }

        return (CSharpCompilation)output;
    }

    // The generated record types, by their Avro full name (their static Schema says it, whatever the C# name became).
    private static Dictionary<string, Type> TypesBySchema(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsClass && t.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is RecordSchema)
            .ToDictionary(t => ((RecordSchema)t.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!).FullName, StringComparer.Ordinal);

    private static void RoundTrip(Type type, RecordSchema schema, string json)
    {
        var fromSequence = type.GetMethod("FromAvroBytes", [typeof(ReadOnlySequence<byte>).MakeByRefType()])!;
        var toBytes = type.GetMethod("ToAvroBytes", Type.EmptyTypes)!;
        byte[] Read(byte[] data) => (byte[])toBytes.Invoke(fromSequence.Invoke(null, [new ReadOnlySequence<byte>(data)]), null)!;

        var generic = GenericDatumReader.Create(schema);
        var genericWriter = GenericDatumWriter.Create(schema);
        var exact = !json.Contains("logicalType", StringComparison.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            if (new RandomValues(i).TryCreate(schema) is not { } value)
            {
                return;
            }

            // The generated reader reads what the generic writer wrote; what it writes, the generic reader reads back
            // to the same bytes, and reading it again changes nothing. Logical values may be re-encoded (a decimal in
            // fewer bytes), so the bytes are compared exactly only without them.
            var bytes = genericWriter.WriteToArray(value);
            var written = Read(bytes);
            Expect(genericWriter.WriteToArray(generic.Read(written)), written, "the generic reader reads the generated writer's bytes back", json);
            Expect(Read(written), written, "reading the generated bytes again", json);
            if (exact)
            {
                Expect(written, bytes, "the generated round trip", json);
            }

            // The same through a container file, which goes through IAvroSerializable<T>.
            Expect(ThroughContainer(type, schema, value), written, "a container file", json);
        }

        // new T() has every field's schema default, as a reader fills in a field the data lacks.
        var instance = Activator.CreateInstance(type)!;
        if (schema.Fields.All(f => f.DefaultValue is not null))
        {
            var empty = AvroSchema.Parse($$"""{"type":"record","name":"{{schema.FullName}}","fields":[]}""");
            var defaults = genericWriter.WriteToArray(GenericDatumReader.Create(empty, schema).Read([]));
            Expect((byte[])toBytes.Invoke(instance, null)!, Read(defaults), "new T() against the resolving reader's defaults", json);
        }

        // Another version of the schema: a field dropped, one added, and a promotion.
        if (OlderVersion(json) is { } older && new RandomValues(7).TryCreate(older) is { } olderValue)
        {
            var resolved = GenericDatumWriter.Create(schema).WriteToArray(GenericDatumReader.Create(older, schema).Read(GenericDatumWriter.Create(older).WriteToArray(olderValue)));
            Expect(ThroughContainer(type, older, olderValue), Read(resolved), $"resolution from {older.ToJson()}", json);
        }
    }

    // Writes the value in a container file of its schema, and reads it with AvroFileReader.Open<T>, which resolves
    // another version of the type's schema.
    private static byte[] ThroughContainer(Type type, AvroSchema writerSchema, AvroValue value)
    {
        using var file = new MemoryStream();
        using (var writer = AvroFileWriter.CreateGeneric(file, writerSchema, new AvroFileWriterOptions { LeaveOpen = true }))
        {
            writer.Write(value);
        }

        file.Position = 0;
        var open = typeof(AvroFileReader).GetMethods()
            .Single(m => string.Equals(m.Name, nameof(AvroFileReader.Open), StringComparison.Ordinal) && m.IsGenericMethod && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(AvroFileReaderOptions))
            .MakeGenericMethod(type);
        using var reader = (IDisposable)open.Invoke(null, [file, null])!;
        var read = ((System.Collections.IEnumerable)reader.GetType().GetMethod("ReadAll")!.Invoke(reader, null)!).Cast<object>().Single();
        return (byte[])type.GetMethod("ToAvroBytes", Type.EmptyTypes)!.Invoke(read, null)!;
    }

    /// <summary>
    /// A writer schema the generated type resolves from: without the first field that has a default and defines no
    /// named type, with a writer-only field, and with the first long, double or string field written as the type that
    /// promotes to it. Null when there is nothing to change.
    /// </summary>
    private static RecordSchema? OlderVersion(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var fields = root["fields"]!.AsArray();
        var changed = false;
        var dropped = fields.FirstOrDefault(f => f!["default"] is not null && !f!["type"]!.ToJsonString().Contains("\"name\"", StringComparison.Ordinal));
        if (dropped is not null)
        {
            fields.Remove(dropped);
            changed = true;
        }

        foreach (var field in fields)
        {
            var type = field!["type"]!.ToJsonString();
            var promoted = type switch { "\"long\"" => "int", "\"double\"" => "float", "\"string\"" => "bytes", _ => null };
            if (promoted is not null)
            {
                // Its default may not fit the narrower type, and a writer's defaults are not used.
                field["type"] = promoted;
                field.AsObject().Remove("default");
                changed = true;
                break;
            }
        }

        fields.Add(JsonNode.Parse("""{"name":"writer_only","type":["null","string"]}"""));
        return changed ? (RecordSchema)AvroSchema.Parse(root.ToJsonString()) : null;
    }

    private static void Expect(byte[] actual, byte[] expected, string what, string json)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{what}: {Convert.ToHexString(actual)}, expected {Convert.ToHexString(expected)}, for {json}");
        }
    }
}
