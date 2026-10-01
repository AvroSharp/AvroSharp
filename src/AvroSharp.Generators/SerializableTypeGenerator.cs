using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AvroSharp.Generators;

/// <summary>
/// Generates an Avro schema and serializers for each <see langword="partial"/> class marked
/// <c>[AvroSerializable]</c>, from its members (docs/design.md §6.5): the members that types generated from
/// <c>.avsc</c> files have, so the rest of AvroSharp takes them alike.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SerializableTypeGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.ForAttributeWithMetadataName(
                SerializableTypeAnalyzer.AttributeName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (attributeContext, cancellationToken) => SerializableTypeAnalyzer.Analyze(
                    (INamedTypeSymbol)attributeContext.TargetSymbol,
                    attributeContext.SemanticModel.Compilation,
                    attributeContext.SemanticModel.SyntaxTree.Options is CSharpParseOptions options ? options.LanguageVersion.MapSpecifiedToEffectiveVersion() : LanguageVersion.CSharp7_3,
                    cancellationToken))
            .WithTrackingName("Analyze");

        context.RegisterSourceOutput(types, static (output, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
            {
                output.ReportDiagnostic(SerializableTypeDiagnostics.Create(diagnostic));
            }

            if (model.Source is { } source)
            {
                output.AddSource(model.HintName, SourceText.From(source, System.Text.Encoding.UTF8));
            }
        });
    }
}
