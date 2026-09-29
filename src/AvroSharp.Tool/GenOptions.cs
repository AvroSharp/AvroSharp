using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.CommandLine;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

namespace AvroSharp.Tool;

/// <summary>The <c>gen</c> options that become <see cref="CodeGenOptions"/>, one for each of its settings.</summary>
internal sealed class GenOptions
{
    private const string Native = "native";
    private const string Raw = "raw";
    private const string Pascal = "pascal";
    private const string Avro = "avro";

    private readonly Option<string?> _namespace = new("--namespace", "-n")
    {
        Description = "The C# namespace for types that have no Avro namespace. Without it they go in the global namespace.",
    };

    private readonly Option<string[]> _namespaceMap = new("--namespace-map", "-m")
    {
        Description = "avro.namespace:CSharp.Namespace: generate the types of an Avro namespace, and of the namespaces under it, in another C# namespace, as avrogen's --namespace does. The schema, and so the data, is unchanged. Repeat the option for several; the longest match wins.",
        Arity = ArgumentArity.OneOrMore,
    };

    private readonly Option<string> _logicalTypes = new("--logical-types")
    {
        Description = "native: .NET types for logical types (DateOnly, DateTimeOffset, Guid, decimal...). raw: their underlying Avro types.",
        DefaultValueFactory = _ => Native,
    };

    private readonly Option<string?> _propertyNames = new("--property-names")
    {
        Description = "pascal: PascalCase properties. avro: the Avro field names as written, as Apache.Avro's avrogen does. Default: pascal, or avro with --apache-compatible.",
    };

    private readonly Option<bool> _apache = new("--apache-compatible")
    {
        Description = "Also make the types work with Apache.Avro's SpecificDatumWriter/Reader and with code written for avrogen classes. The project then needs Apache.Avro.",
    };

    private readonly Option<bool> _noNullable = new("--no-nullable")
    {
        Description = "No nullable reference type annotations, so the code compiles as C# 7.3 (the default for netstandard2.0 and .NET Framework projects).",
    };

    private readonly Option<bool> _noDateOnly = new("--no-date-only")
    {
        Description = "For targets without DateOnly and TimeOnly (netstandard, .NET Framework): date becomes DateTime and time-* TimeSpan.",
    };

    private readonly Option<int> _languageVersion = new("--language-version")
    {
        Description = "The major C# version the code may use (7 or later). With 11 or later, .NET 8+ targets also get IAvroSerializable<T>.",
        DefaultValueFactory = _ => CodeGenOptions.Default.LanguageVersion,
    };

    public GenOptions()
    {
        _logicalTypes.AcceptOnlyFromAmong(Native, Raw);
        _propertyNames.AcceptOnlyFromAmong(Pascal, Avro);
        _languageVersion.Validators.Add(result =>
        {
            // A value that is not a number is reported by the parser; reading it here would throw.
            if (result.Tokens.Count > 0 && int.TryParse(result.Tokens[^1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version < 7)
            {
                result.AddError("--language-version must be 7 or later.");
            }
        });
    }

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { _namespace, _namespaceMap, _logicalTypes, _propertyNames, _apache, _noNullable, _noDateOnly, _languageVersion })
        {
            command.Options.Add(option);
        }

        command.Validators.Add(result =>
        {
            if (result.GetValue(_namespace) is { } ns && ns.Contains(':', StringComparison.Ordinal))
            {
                result.AddError("--namespace is the C# namespace for types without an Avro namespace; to map namespaces as avrogen's --namespace does, use --namespace-map avro.namespace:CSharp.Namespace.");
            }
            else if (result.GetValue(_namespace) is { } other && !IsNamespace(other))
            {
                // "My Models" or "1abc" gave exit code 0 and code that did not compile (#131).
                result.AddError($"--namespace '{other}' is not a C# namespace (names of letters, digits and underscores, separated by dots).");
            }

            if (result.GetValue(_namespaceMap) is { Length: > 0 } && result.GetValue(_apache))
            {
                result.AddError("--namespace-map cannot be combined with --apache-compatible: Apache.Avro finds generated types by the schema's full name.");
            }
        });
        _namespaceMap.Validators.Add(result =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in result.Tokens)
            {
                if (!TryParseMapping(token.Value, out var avro, out _))
                {
                    result.AddError($"--namespace-map '{token.Value}' is not avro.namespace:CSharp.Namespace (names separated by dots, on both sides of the colon).");
                }
                else if (!seen.Add(avro))
                {
                    result.AddError($"--namespace-map maps '{avro}' more than once.");
                }
            }
        });
    }

    public CodeGenOptions ToCodeGenOptions(ParseResult result) => new()
    {
        DefaultNamespace = result.GetValue(_namespace),
        NamespaceMapping = NamespaceMapping(result.GetValue(_namespaceMap)),
        LogicalTypes = string.Equals(result.GetValue(_logicalTypes), Raw, StringComparison.Ordinal) ? LogicalTypeMapping.Raw : LogicalTypeMapping.Native,
        PropertyNaming = result.GetValue(_propertyNames) switch
        {
            Pascal => PropertyNaming.PascalCase,
            Avro => PropertyNaming.Avro,
            _ => null,
        },
        ApacheCompatible = result.GetValue(_apache),
        NullableAnnotations = !result.GetValue(_noNullable),
        TargetHasDateOnly = !result.GetValue(_noDateOnly),
        LanguageVersion = result.GetValue(_languageVersion),
    };

    private static Dictionary<string, string>? NamespaceMapping(string[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (TryParseMapping(value, out var avro, out var csharp))
            {
                mapping[avro] = csharp;
            }
        }

        return mapping;
    }

    private static bool TryParseMapping(string value, out string avro, out string csharp)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        avro = colon < 0 ? string.Empty : value[..colon].Trim();
        csharp = colon < 0 ? string.Empty : value[(colon + 1)..].Trim();
        return IsNamespace(avro) && IsNamespace(csharp);
    }

    private static bool IsNamespace(string text) => text.Length > 0 && text.Split('.').All(part => AvroNames.IsValidName(part));
}