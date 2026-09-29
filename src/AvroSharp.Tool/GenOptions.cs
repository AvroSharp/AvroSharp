using System;
using System.Globalization;
using System.CommandLine;
using AvroSharp.CodeGen;

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
        Description = "The major C# version the code may use (7 or later). With 11 or later, .NET 8+ targets also get the UTF-8 schema literal and IAvroSerializable<T>.",
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
        foreach (var option in new Option[] { _namespace, _logicalTypes, _propertyNames, _apache, _noNullable, _noDateOnly, _languageVersion })
        {
            command.Options.Add(option);
        }
    }

    public CodeGenOptions ToCodeGenOptions(ParseResult result) => new()
    {
        DefaultNamespace = result.GetValue(_namespace),
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
}
