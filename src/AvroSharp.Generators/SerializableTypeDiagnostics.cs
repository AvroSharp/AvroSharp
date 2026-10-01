using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace AvroSharp.Generators;

/// <summary>The diagnostics of <see cref="SerializableTypeGenerator"/> (AnalyzerReleases.Unshipped.md).</summary>
internal static class SerializableTypeDiagnostics
{
    public const string GenerationFailed = "AVROGEN118";
    public const string NotPartial = "AVROGEN101";
    public const string UnsupportedType = "AVROGEN102";
    public const string DecimalWithoutPrecision = "AVROGEN103";
    public const string InvalidName = "AVROGEN104";
    public const string DuplicateName = "AVROGEN105";
    public const string InvalidDefault = "AVROGEN106";
    public const string GenericOrNested = "AVROGEN107";
    public const string PrimaryConstructor = "AVROGEN108";
    public const string AmbiguousOrder = "AVROGEN109";
    public const string UnionMismatch = "AVROGEN110";
    public const string NotSerializable = "AVROGEN111";
    public const string MisappliedAttribute = "AVROGEN112";
    public const string NameCollision = "AVROGEN113";
    public const string DateTimeWithoutLogicalType = "AVROGEN114";
    public const string InitOnly = "AVROGEN115";
    public const string EnumValues = "AVROGEN116";
    public const string ReservedName = "AVROGEN117";

    private const string Category = "AvroSharp";

    private static readonly DiagnosticDescriptor s_generationFailed = new("AVROGEN118", "Avro code generation failed for an [AvroSerializable] type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_notPartial = new("AVROGEN101", "[AvroSerializable] type is not a partial class", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_unsupportedType = new("AVROGEN102", "Member type has no Avro mapping", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_decimalWithoutPrecision = new("AVROGEN103", "decimal member without [AvroDecimal]", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_invalidName = new("AVROGEN104", "Invalid Avro name", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_duplicateName = new("AVROGEN105", "Two members have the same Avro field name", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_invalidDefault = new("AVROGEN106", "Invalid Avro default", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_genericOrNested = new("AVROGEN107", "[AvroSerializable] type is generic or nested", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_primaryConstructor = new("AVROGEN108", "[AvroSerializable] type has a primary constructor", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_ambiguousOrder = new("AVROGEN109", "Field order is ambiguous", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_unionMismatch = new("AVROGEN110", "[AvroUnion] does not fit the member", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_notSerializable = new("AVROGEN111", "Member uses a class without [AvroSerializable]", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_misappliedAttribute = new("AVROGEN112", "Avro attribute does not apply", "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_nameCollision = new("AVROGEN113", "Avro name defined by two types", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_dateTimeWithoutLogicalType = new("AVROGEN114", "DateTime member without a logical type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_initOnly = new("AVROGEN115", "init-only member", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_enumValues = new("AVROGEN116", "Enum values are not 0, 1, 2 and so on", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor s_reservedName = new("AVROGEN117", "Member has the name of a generated member", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly Dictionary<string, DiagnosticDescriptor> s_descriptors = new DiagnosticDescriptor[]
    {
        s_generationFailed,
        s_notPartial,
        s_unsupportedType,
        s_decimalWithoutPrecision,
        s_invalidName,
        s_duplicateName,
        s_invalidDefault,
        s_genericOrNested,
        s_primaryConstructor,
        s_ambiguousOrder,
        s_unionMismatch,
        s_notSerializable,
        s_misappliedAttribute,
        s_nameCollision,
        s_dateTimeWithoutLogicalType,
        s_initOnly,
        s_enumValues,
        s_reservedName,
    }.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static bool IsWarning(string id) => string.Equals(id, MisappliedAttribute, StringComparison.Ordinal);

    public static Diagnostic Create(DiagnosticInfo info) => Diagnostic.Create(s_descriptors[info.Id], info.ToLocation(), info.Message);
}
