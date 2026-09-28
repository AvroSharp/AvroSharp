using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AvroSharp.Generators.Tests;

/// <summary>
/// The consumer tests' schemas (every type, logical type and collection shape they exercise at run time), generated
/// in-process with each option the generator has and compiled without warnings. The consumer projects run the
/// generator inside the compiler, where coverage is not measured; this measures it, and checks the option
/// combinations those projects don't build.
/// </summary>
public class ConsumerSchemaTests
{
    [Test]
    [Arguments(LanguageVersion.Latest, false, null, null)]
    [Arguments(LanguageVersion.CSharp7_3, false, null, null)]
    [Arguments(LanguageVersion.CSharp8, false, null, null)]
    [Arguments(LanguageVersion.Latest, true, null, null)]
    [Arguments(LanguageVersion.CSharp7_3, true, null, null)]
    [Arguments(LanguageVersion.Latest, false, "raw", null)]
    [Arguments(LanguageVersion.CSharp7_3, false, "raw", "avro")]
    [Arguments(LanguageVersion.Latest, true, "raw", "avro")]
    public async Task ConsumerSchemas_CompileWithoutWarnings(LanguageVersion version, bool apache, string? logicalTypes, string? propertyNames)
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ConsumerSchemas"), "*.avsc")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

        var (sources, generatorDiagnostics, compileDiagnostics) = GeneratorHarness.Run(
            files, "Generated.Default", logicalTypes: logicalTypes, apacheCompatible: apache, referenceApache: apache, languageVersion: version, propertyNames: propertyNames);

        await Assert.That(files.Length).IsGreaterThanOrEqualTo(9);
        await Assert.That(generatorDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
        await Assert.That(sources.Length).IsGreaterThan(files.Length);
        await Assert.That(compileDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())).IsEmpty();
    }
}
