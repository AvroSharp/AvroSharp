using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using AvroSharp.CodeGen;
using AvroSharp.Schemas;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AvroSharp.Generators;

/// <summary>
/// Builds the schema of an <c>[AvroSerializable]</c> type from its members (docs/design.md §6.5), with every record
/// and enum it uses, checks each member against the C# type its field is read as, and generates the serializers.
/// </summary>
internal sealed class SerializableTypeAnalyzer
{
    public const string AttributeName = Attributes + "AvroSerializableAttribute";

    private const string Attributes = "AvroSharp.Serialization.";
    private const string NameAttribute = Attributes + "AvroNameAttribute";
    private const string AliasAttribute = Attributes + "AvroAliasAttribute";
    private const string DocAttribute = Attributes + "AvroDocAttribute";
    private const string IgnoreAttribute = Attributes + "AvroIgnoreAttribute";
    private const string DefaultAttribute = Attributes + "AvroDefaultAttribute";
    private const string DecimalAttribute = Attributes + "AvroDecimalAttribute";
    private const string FixedAttribute = Attributes + "AvroFixedAttribute";
    private const string LogicalTypeAttribute = Attributes + "AvroLogicalTypeAttribute";
    private const string UnionAttribute = Attributes + "AvroUnionAttribute";
    private const string EnumDefaultAttribute = Attributes + "AvroEnumDefaultAttribute";
    private const string FieldPositionAttribute = Attributes + "AvroFieldPositionAttribute";
    private const string DefaultsAttribute = Attributes + "AvroSerializableDefaultsAttribute";

    private const int CamelCase = 1;

    private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly Compilation _compilation;
    private readonly CancellationToken _cancellationToken;
    private readonly List<DiagnosticInfo> _diagnostics;
    private readonly int _assemblyNaming;

    // Every named type written so far, by Avro full name: a later use refers to it by name, and another C# type of the
    // same name is a collision.
    private readonly Dictionary<string, ISymbol> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fixedSizes = new(StringComparer.Ordinal);
    private readonly List<RecordInfo> _records = [];
    private readonly List<DeclaredEnum> _enums = [];
    private readonly List<string> _byteArrayFixed = [];

    private SerializableTypeAnalyzer(Compilation compilation, List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        _compilation = compilation;
        _diagnostics = diagnostics;
        _cancellationToken = cancellationToken;
        _assemblyNaming = compilation.Assembly.GetAttributes()
            .Where(a => Is(a, DefaultsAttribute))
            .Select(a => Named<int?>(a, "FieldNames") ?? 0)
            .FirstOrDefault();
    }

    private Utf8JsonWriter Json { get; set; } = null!;

    /// <summary>Analyzes <paramref name="type"/> and generates its serializers, or reports why it can't.</summary>
    public static SerializableTypeModel Analyze(INamedTypeSymbol type, Compilation compilation, LanguageVersion languageVersion, CancellationToken cancellationToken)
    {
        var hintName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).Replace('<', '_').Replace('>', '_') + ".Avro.g.cs";
        var diagnostics = new List<DiagnosticInfo>();
        var analyzer = new SerializableTypeAnalyzer(compilation, diagnostics, cancellationToken);
        var source = analyzer.Run(type, MajorVersion(languageVersion));
        return new SerializableTypeModel(hintName, diagnostics.Any(IsError) ? null : source, new EquatableArray<DiagnosticInfo>(diagnostics));
    }

    private static bool IsError(DiagnosticInfo diagnostic) => !SerializableTypeDiagnostics.IsWarning(diagnostic.Id);

    private static int MajorVersion(LanguageVersion version) => version < LanguageVersion.CSharp7_2 ? 0 : (int)version / 100;

    private string? Run(INamedTypeSymbol type, int languageVersion)
    {
        if (!CheckDeclaredType(type, _diagnostics))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            Json = json;
            WriteRecord(type);
        }

        if (_diagnostics.Any(IsError))
        {
            return null;
        }

        var schemaJson = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return Generate(type, schemaJson, languageVersion);
    }

    private string? Generate(INamedTypeSymbol type, string schemaJson, int languageVersion)
    {
        RecordSchema schema;
        try
        {
            schema = (RecordSchema)AvroSchema.Parse(schemaJson);
        }
        catch (AvroSchemaException ex)
        {
            Report(SerializableTypeDiagnostics.InvalidDefault, $"The schema of {type.Name} is not valid: {ex.Message}", type);
            return null;
        }

        var records = new Dictionary<string, RecordSchema>(StringComparer.Ordinal);
        Collect(schema, records);
        var declared = _records.Select(r => new DeclaredRecord(records[r.FullName], Namespace(r.Type), r.Type.Name, r.Type.IsRecord, [.. r.Members.Select(m => Identifier(m.Name))], r.Type.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared))).ToList();
        var options = new CodeGenOptions
        {
            LanguageVersion = languageVersion,
            NullableAnnotations = languageVersion >= 8,
            TargetHasDateOnly = _compilation.GetTypeByMetadataName("System.DateOnly") is not null,
            ByteArrayFixed = _byteArrayFixed,
        };

        if (!CheckMemberTypes(declared, options))
        {
            return null;
        }

        try
        {
            return CSharpCodeGenerator.GenerateDeclared([declared[0]], declared, _enums, options)[0].Text;
        }
        catch (Exception ex) when (ex is AvroException or ArgumentException or InvalidOperationException)
        {
            Report(SerializableTypeDiagnostics.GenerationFailed, ex.Message, type);
            return null;
        }
    }

    // A member must be of the type its field is read as. The mapping is built to agree, so a mismatch is a type this
    // version can't map (TimeSpan on .NET 8, or a logical type on a raw number).
    private bool CheckMemberTypes(List<DeclaredRecord> declared, CodeGenOptions options)
    {
        var plain = new CodeGenOptions
        {
            LanguageVersion = options.LanguageVersion,
            NullableAnnotations = false,
            TargetHasDateOnly = options.TargetHasDateOnly,
            ByteArrayFixed = options.ByteArrayFixed,
        };

        var ok = true;
        for (var i = 0; i < declared.Count; i++)
        {
            var expected = CSharpCodeGenerator.DeclaredFieldTypes(declared[i], declared, _enums, plain);
            var members = _records[i].Members;
            for (var j = 0; j < members.Count; j++)
            {
                var actual = MemberType(members[j]).ToDisplayString(s_typeFormat);
                if (!string.Equals(actual, expected[j], StringComparison.Ordinal))
                {
                    Report(
                        SerializableTypeDiagnostics.UnsupportedType,
                        $"{members[j].Name} is a {actual}, but its Avro field {CSharpNames.Literal(declared[i].Schema.Fields[j].Schema.CanonicalForm)} is read as {expected[j]}: make it that type, or change the field with an attribute.",
                        members[j]);
                    ok = false;
                }
            }
        }

        return ok;
    }

    private static void Collect(AvroSchema schema, Dictionary<string, RecordSchema> records)
    {
        switch (schema)
        {
            case RecordSchema record when !records.ContainsKey(record.FullName):
                records.Add(record.FullName, record);
                foreach (var field in record.Fields)
                {
                    Collect(field.Schema, records);
                }

                break;
            case ArraySchema array:
                Collect(array.Items, records);
                break;
            case MapSchema map:
                Collect(map.Values, records);
                break;
            case UnionSchema union:
                foreach (var branch in union.Branches)
                {
                    Collect(branch, records);
                }

                break;
        }
    }

    // --- Declared types ---

    /// <summary>Checks that a type can have generated serializers: a top-level, non-generic, partial class or record class.</summary>
    private bool CheckDeclaredType(INamedTypeSymbol type, List<DiagnosticInfo> sink)
    {
        var errors = sink.Count;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic
            || type.DeclaringSyntaxReferences.Select(r => r.GetSyntax(_cancellationToken)).Any(s => s is not TypeDeclarationSyntax declaration || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            sink.Add(At(SerializableTypeDiagnostics.NotPartial, $"{type.Name} must be a partial class or record class, not abstract or static, for [AvroSerializable] to add its serializers.", type));
        }

        if (type.ContainingType is not null || type.IsGenericType)
        {
            sink.Add(At(SerializableTypeDiagnostics.GenericOrNested, $"{type.Name} is {(type.IsGenericType ? "generic" : "nested in another type")}; [AvroSerializable] supports top-level, non-generic types.", type));
        }

        if (type.DeclaringSyntaxReferences.Any(r => r.GetSyntax(_cancellationToken) is TypeDeclarationSyntax { ParameterList: not null }))
        {
            sink.Add(At(SerializableTypeDiagnostics.PrimaryConstructor, $"{type.Name} has a primary constructor; [AvroSerializable] types need settable properties instead, for now.", type));
        }

        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && CSharpCodeGenerator.DeclaredRecordMembers.Contains(m.Name, StringComparer.Ordinal)))
        {
            sink.Add(At(SerializableTypeDiagnostics.ReservedName, $"{type.Name}.{member.Name} has the name of a member that [AvroSerializable] adds; rename it.", member));
        }

        return sink.Count == errors;
    }

    private void WriteRecord(INamedTypeSymbol type)
    {
        var attribute = type.GetAttributes().First(a => Is(a, AttributeName));
        var name = Named<string>(attribute, "Name") ?? type.Name;
        var ns = Named<string>(attribute, "Namespace") ?? Namespace(type);
        var fullName = ns is null ? name : ns + "." + name;
        if (!Define(fullName, type, type))
        {
            return;
        }

        CheckName(name, ns, type);
        var members = Members(type);
        _records.Add(new RecordInfo(type, fullName, members));

        Json.WriteStartObject();
        Json.WriteString("type", "record");
        Json.WriteString("name", name);
        Json.WriteString("namespace", ns ?? string.Empty);
        WriteDoc(Named<string>(attribute, "Doc") ?? Summary(type));
        WriteAliases(type);
        Json.WriteStartArray("fields");
        var naming = attribute.NamedArguments.Any(a => string.Equals(a.Key, "FieldNames", StringComparison.Ordinal))
            ? Named<int?>(attribute, "FieldNames") ?? 0
            : _assemblyNaming;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            WriteField(member, naming, names);
        }

        Json.WriteEndArray();
        Json.WriteEndObject();
    }

    /// <summary>The fields' members: public settable properties and public fields, base types first, in order.</summary>
    private List<ISymbol> Members(INamedTypeSymbol type)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            chain.Push(current);
        }

        var members = new List<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = false;
        while (chain.Count > 0)
        {
            var declared = chain.Pop().GetMembers().Where(IsField).Where(m => seen.Add(m.Name)).ToList();
            ambiguous |= declared.SelectMany(m => m.DeclaringSyntaxReferences).Select(r => r.SyntaxTree).Distinct().Count() > 1;
            members.AddRange(declared);
        }

        var orders = members.Select(m => Constructor<int?>(Attribute(m, FieldPositionAttribute)) ?? -1).ToList();
        if (!ambiguous && orders.All(o => o < 0))
        {
            return members;
        }

        if (orders.Any(o => o < 0) || orders.Distinct().Count() != orders.Count)
        {
            Report(
                SerializableTypeDiagnostics.AmbiguousOrder,
                $"The fields of {type.Name} are declared in more than one file, or some have [AvroFieldPosition(n)]: give every field a distinct [AvroFieldPosition(n)], since the compiler doesn't fix the order across files.",
                type);
            return members;
        }

        return [.. members.Select((m, i) => (Member: m, Order: orders[i])).OrderBy(p => p.Order).Select(p => p.Member)];
    }

    private bool IsField(ISymbol member)
    {
        if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public || Attribute(member, IgnoreAttribute) is not null)
        {
            return false;
        }

        switch (member)
        {
            case IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public, SetMethod: { } setter } property:
                if (setter.IsInitOnly)
                {
                    Report(SerializableTypeDiagnostics.InitOnly, $"{property.Name} is init-only; [AvroSerializable] types need settable properties for now (or mark it [AvroIgnore]).", property);
                    return false;
                }

                return setter.DeclaredAccessibility == Accessibility.Public;
            case IFieldSymbol { IsConst: false, IsReadOnly: false }:
                return true;
            default:
                return false;
        }
    }

    private void WriteField(ISymbol member, int naming, HashSet<string> names)
    {
        var name = Constructor<string>(Attribute(member, NameAttribute)) ?? (naming == CamelCase ? JsonNamingPolicy.CamelCase.ConvertName(member.Name) : member.Name);
        if (!AvroNames.IsValidName(name.AsSpan()))
        {
            Report(SerializableTypeDiagnostics.InvalidName, $"'{name}', the field name of {member.Name}, is not a valid Avro name ([A-Za-z_][A-Za-z0-9_]*); rename it with [AvroName].", member);
        }

        if (!names.Add(name))
        {
            Report(SerializableTypeDiagnostics.DuplicateName, $"Two members have the Avro field name '{name}'.", member);
        }

        Json.WriteStartObject();
        Json.WriteString("name", name);
        Json.WritePropertyName("type");
        var nullable = WriteMemberSchema(member, MemberType(member));
        WriteDoc(Constructor<string>(Attribute(member, DocAttribute)) ?? Summary(member));
        if (Constructor<string>(Attribute(member, DefaultAttribute)) is { } json)
        {
            WriteDefault(json, member);
        }
        else if (nullable)
        {
            Json.WriteNull("default");
        }

        WriteAliases(member);
        Json.WriteEndObject();
    }

    private void WriteDefault(string json, ISymbol member)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Json.WritePropertyName("default");
            document.RootElement.WriteTo(Json);
        }
        catch (JsonException ex)
        {
            Report(SerializableTypeDiagnostics.InvalidDefault, $"The [AvroDefault] of {member.Name} is not JSON: {ex.Message}", member);
        }
    }

    // --- Member schemas ---

    /// <summary>Writes a member's schema; returns whether it is nullable (a union with null first).</summary>
    private bool WriteMemberSchema(ISymbol member, ITypeSymbol type)
    {
        var attributes = new MemberAttributes(member);
        if (Attribute(member, UnionAttribute) is { } union)
        {
            return WriteUnion(member, type, union);
        }

        var nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue)
        {
            nullable = true;
            type = nullableValue.TypeArguments[0];
        }
        else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
            type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        if (nullable)
        {
            Json.WriteStartArray();
            Json.WriteStringValue("null");
        }

        WriteValue(type, member, attributes);
        if (nullable)
        {
            Json.WriteEndArray();
        }

        attributes.ReportUnused(this);
        return nullable;
    }

    private bool WriteUnion(ISymbol member, ITypeSymbol type, AttributeData union)
    {
        if (type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).SpecialType != SpecialType.System_Object)
        {
            Report(SerializableTypeDiagnostics.UnionMismatch, $"[AvroUnion] is for object members; {member.Name} is a {type.ToDisplayString()}.", member);
        }

        var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        Json.WriteStartArray();
        if (nullable)
        {
            Json.WriteStringValue("null");
        }

        var branches = union.ConstructorArguments.FirstOrDefault().Values;
        if (branches.IsDefaultOrEmpty)
        {
            Report(SerializableTypeDiagnostics.UnionMismatch, $"The [AvroUnion] of {member.Name} lists no types.", member);
        }
        else
        {
            foreach (var branch in branches)
            {
                if (branch.Value is INamedTypeSymbol branchType && branchType.TypeKind == TypeKind.Class)
                {
                    WriteRecordReference(branchType, member);
                }
                else
                {
                    Report(SerializableTypeDiagnostics.UnionMismatch, $"The [AvroUnion] of {member.Name} lists {branch.Value}, which is not an [AvroSerializable] class.", member);
                    Json.WriteStringValue("null");
                }
            }
        }

        Json.WriteEndArray();
        return nullable;
    }

    private void WriteValue(ITypeSymbol type, ISymbol member, MemberAttributes? attributes)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
        {
            WriteBytes(member, attributes);
            return;
        }

        var primitive = type.SpecialType switch
        {
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_Int32 => "int",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_String => "string",
            _ => null,
        };
        if (primitive is not null)
        {
            if (attributes?.LogicalType is { } logicalType && primitive is "int" or "long" or "string")
            {
                attributes.LogicalTypeUsed = true;
                WriteRawLogical(member, primitive, Constructor<string>(logicalType));
                return;
            }

            Json.WriteStringValue(primitive);
            return;
        }

        if (type.SpecialType == SpecialType.System_Decimal)
        {
            WriteDecimal(member, attributes);
            return;
        }

        if (type is INamedTypeSymbol named && WriteNamed(named, member, attributes))
        {
            return;
        }

        Report(
            SerializableTypeDiagnostics.UnsupportedType,
            $"{member.Name} is a {type.ToDisplayString()}, which has no Avro mapping. Supported: bool, int, long, float, double, string, byte[], decimal, Guid, DateOnly, TimeOnly, DateTimeOffset, DateTime (with [AvroLogicalType]), enums, [AvroSerializable] classes, List<T>, Dictionary<string, T>, their nullable forms, and object with [AvroUnion].",
            member);
        Json.WriteStringValue("null");
    }

    private bool WriteNamed(INamedTypeSymbol type, ISymbol member, MemberAttributes? attributes)
    {
        var name = type.ToDisplayString();
        switch (name)
        {
            case "System.Guid":
                WriteGuid(member, attributes);
                return true;
            case "System.DateTimeOffset":
                WriteLogical(member, attributes, "timestamp-micros", ["timestamp-millis", "timestamp-micros"]);
                return true;
            case "System.DateTime":
                WriteLogical(member, attributes, null, ["local-timestamp-millis", "local-timestamp-micros", "date"]);
                return true;
            case "System.DateOnly":
                WriteLogical(member, attributes, "date", ["date"]);
                return true;
            case "System.TimeOnly":
                WriteLogical(member, attributes, "time-micros", ["time-millis", "time-micros"]);
                return true;
            case "System.TimeSpan":
                WriteLogical(member, attributes, null, ["time-millis", "time-micros"]);
                return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            WriteEnum(type, member);
            return true;
        }

        if (WriteCollection(type, member))
        {
            return true;
        }

        if (type.TypeKind == TypeKind.Class && type.SpecialType == SpecialType.None)
        {
            WriteRecordReference(type, member);
            return true;
        }

        return false;
    }

    private bool WriteCollection(INamedTypeSymbol type, ISymbol member)
    {
        var definition = type.OriginalDefinition.ToDisplayString();
        if (string.Equals(definition, "System.Collections.Generic.List<T>", StringComparison.Ordinal))
        {
            Json.WriteStartObject();
            Json.WriteString("type", "array");
            Json.WritePropertyName("items");
            WriteElement(type.TypeArguments[0], member);
            Json.WriteEndObject();
            return true;
        }

        if (string.Equals(definition, "System.Collections.Generic.Dictionary<TKey, TValue>", StringComparison.Ordinal))
        {
            if (type.TypeArguments[0].SpecialType != SpecialType.System_String)
            {
                Report(SerializableTypeDiagnostics.UnsupportedType, $"{member.Name} is a dictionary with {type.TypeArguments[0].ToDisplayString()} keys; Avro maps have string keys.", member);
            }

            Json.WriteStartObject();
            Json.WriteString("type", "map");
            Json.WritePropertyName("values");
            WriteElement(type.TypeArguments[1], member);
            Json.WriteEndObject();
            return true;
        }

        return false;
    }

    /// <summary>An array's items or a map's values: nullable forms, but no member attributes.</summary>
    private void WriteElement(ITypeSymbol type, ISymbol member)
    {
        var nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableValue)
        {
            nullable = true;
            type = nullableValue.TypeArguments[0];
        }
        else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
            type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        if (nullable)
        {
            Json.WriteStartArray();
            Json.WriteStringValue("null");
        }

        WriteValue(type, member, null);
        if (nullable)
        {
            Json.WriteEndArray();
        }
    }

    private void WriteBytes(ISymbol member, MemberAttributes? attributes)
    {
        if (attributes?.Fixed is not { } fixedAttribute)
        {
            Json.WriteStringValue("bytes");
            return;
        }

        attributes.FixedUsed = true;
        var fullName = WriteFixed(member, fixedAttribute, Constructor<int?>(fixedAttribute) ?? 0, logicalType: null);
        if (fullName is not null && !_byteArrayFixed.Contains(fullName, StringComparer.Ordinal))
        {
            _byteArrayFixed.Add(fullName);
        }
    }

    private void WriteGuid(ISymbol member, MemberAttributes? attributes)
    {
        if (attributes?.Fixed is { } fixedAttribute)
        {
            attributes.FixedUsed = true;
            var size = Constructor<int?>(fixedAttribute) ?? 0;
            if (size != 16)
            {
                Report(SerializableTypeDiagnostics.MisappliedAttribute, $"A Guid is 16 bytes; the [AvroFixed] of {member.Name} says {size.ToString(CultureInfo.InvariantCulture)}.", member);
            }

            WriteFixed(member, fixedAttribute, 16, "uuid");
            return;
        }

        WriteLogicalSchema("string", "uuid");
    }

    private void WriteDecimal(ISymbol member, MemberAttributes? attributes)
    {
        if (attributes?.Decimal is not { } decimalAttribute)
        {
            Report(SerializableTypeDiagnostics.DecimalWithoutPrecision, $"{member.Name} is a decimal: give its precision and scale with [AvroDecimal(precision, scale)], since Avro's decimal has no default.", member);
            Json.WriteStringValue("null");
            return;
        }

        attributes.DecimalUsed = true;
        var arguments = decimalAttribute.ConstructorArguments;
        var precision = arguments.Length > 0 && arguments[0].Value is int p ? p : 0;
        var scale = arguments.Length > 1 && arguments[1].Value is int s ? s : 0;
        if (precision is < 1 or > 28 || scale < 0 || scale > precision)
        {
            Report(SerializableTypeDiagnostics.MisappliedAttribute, $"The [AvroDecimal] of {member.Name} needs a precision of 1 to 28 (System.Decimal's digits) and a scale of 0 to the precision.", member);
        }

        if (attributes.Fixed is { } fixedAttribute)
        {
            attributes.FixedUsed = true;
            WriteFixed(member, fixedAttribute, Constructor<int?>(fixedAttribute) ?? 0, "decimal", precision, scale);
            return;
        }

        Json.WriteStartObject();
        Json.WriteString("type", "bytes");
        Json.WriteString("logicalType", "decimal");
        Json.WriteNumber("precision", precision);
        Json.WriteNumber("scale", scale);
        Json.WriteEndObject();
    }

    /// <summary>Writes a fixed type, or a reference to one already written; returns its full name.</summary>
    private string? WriteFixed(ISymbol member, AttributeData fixedAttribute, int size, string? logicalType, int precision = 0, int scale = 0)
    {
        var name = Named<string>(fixedAttribute, "Name") ?? member.Name;
        var ns = Named<string>(fixedAttribute, "Namespace") ?? CurrentNamespace(member);
        var fullName = ns is null ? name : ns + "." + name;
        if (_fixedSizes.TryGetValue(fullName, out var existing))
        {
            if (existing != size)
            {
                Report(SerializableTypeDiagnostics.NameCollision, $"The fixed type {fullName} of {member.Name} is defined with another size elsewhere; give it another name with [AvroFixed(Name = ...)].", member);
            }

            Json.WriteStringValue(fullName);
            return fullName;
        }

        if (!Define(fullName, member, member))
        {
            return null;
        }

        CheckName(name, ns, member);
        _fixedSizes.Add(fullName, size);
        Json.WriteStartObject();
        Json.WriteString("type", "fixed");
        Json.WriteString("name", name);
        Json.WriteString("namespace", ns ?? string.Empty);
        Json.WriteNumber("size", size);
        if (logicalType is not null)
        {
            Json.WriteString("logicalType", logicalType);
        }

        if (string.Equals(logicalType, "decimal", StringComparison.Ordinal))
        {
            Json.WriteNumber("precision", precision);
            Json.WriteNumber("scale", scale);
        }

        Json.WriteEndObject();
        return fullName;
    }

    private void WriteLogical(ISymbol member, MemberAttributes? attributes, string? defaultType, string[] allowed)
    {
        var logical = defaultType;
        if (attributes?.LogicalType is { } logicalAttribute)
        {
            attributes.LogicalTypeUsed = true;
            logical = Constructor<string>(logicalAttribute);
        }

        if (logical is null)
        {
            var dateTime = string.Equals(MemberType(member).WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(), "System.DateTime", StringComparison.Ordinal);
            Report(
                dateTime ? SerializableTypeDiagnostics.DateTimeWithoutLogicalType : SerializableTypeDiagnostics.UnsupportedType,
                dateTime
                    ? $"{member.Name} is a DateTime, whose Kind leaves UTC and local time ambiguous: use DateTimeOffset for a UTC timestamp, or give [AvroLogicalType(\"local-timestamp-micros\")] (or -millis) for local time."
                    : $"{member.Name} needs [AvroLogicalType] with one of: {string.Join(", ", allowed)}.",
                member);
            Json.WriteStringValue("null");
            return;
        }

        if (!allowed.Contains(logical, StringComparer.Ordinal))
        {
            Report(SerializableTypeDiagnostics.MisappliedAttribute, $"The logical type '{logical}' does not apply to {member.Name}; it takes {string.Join(", ", allowed)}.", member);
        }

        var underlying = logical switch
        {
            "date" or "time-millis" => "int",
            "uuid" => "string",
            _ => "long",
        };
        WriteLogicalSchema(underlying, logical);
    }

    // A logical type on a raw number or string: the member keeps the underlying value ("avrosharp.logicalType": "raw"),
    // for values .NET's types can't hold, such as a timestamp of Long.MaxValue.
    private void WriteRawLogical(ISymbol member, string primitive, string? logical)
    {
        var allowed = primitive switch
        {
            "int" => new[] { "date", "time-millis" },
            "long" => ["time-micros", "timestamp-millis", "timestamp-micros", "timestamp-nanos", "local-timestamp-millis", "local-timestamp-micros", "local-timestamp-nanos"],
            _ => ["uuid"],
        };
        if (logical is null || !allowed.Contains(logical, StringComparer.Ordinal))
        {
            Report(SerializableTypeDiagnostics.MisappliedAttribute, $"The logical type '{logical}' does not apply to {member.Name}, a {primitive}; it takes {string.Join(", ", allowed)}.", member);
            Json.WriteStringValue(primitive);
            return;
        }

        Json.WriteStartObject();
        Json.WriteString("type", primitive);
        Json.WriteString("logicalType", logical);
        Json.WriteString("avrosharp.logicalType", "raw");
        Json.WriteEndObject();
    }

    private void WriteLogicalSchema(string type, string logicalType)
    {
        Json.WriteStartObject();
        Json.WriteString("type", type);
        Json.WriteString("logicalType", logicalType);
        Json.WriteEndObject();
    }

    private void WriteRecordReference(INamedTypeSymbol type, ISymbol member)
    {
        if (type.GetAttributes().All(a => !Is(a, AttributeName)))
        {
            Report(SerializableTypeDiagnostics.NotSerializable, $"{member.Name} uses {type.Name}, which is not [AvroSerializable]: add the attribute to it.", member);
            Json.WriteStringValue("null");
            return;
        }

        if (_records.Any(r => SymbolEqualityComparer.Default.Equals(r.Type, type)))
        {
            Json.WriteStringValue(_records.First(r => SymbolEqualityComparer.Default.Equals(r.Type, type)).FullName);
            return;
        }

        // The type reports its own errors; here, only that it can't be used yet.
        if (!CheckDeclaredType(type, []))
        {
            Report(SerializableTypeDiagnostics.NotSerializable, $"{member.Name} uses {type.Name}, whose [AvroSerializable] has errors.", member);
            Json.WriteStringValue("null");
            return;
        }

        WriteRecord(type);
    }

    private void WriteEnum(INamedTypeSymbol type, ISymbol member)
    {
        var name = Constructor<string>(Attribute(type, NameAttribute)) ?? type.Name;
        var ns = Namespace(type);
        var fullName = ns is null ? name : ns + "." + name;
        if (!Define(fullName, type, member))
        {
            return;
        }

        CheckName(name, ns, type);
        _enums.Add(new DeclaredEnum(fullName, ns, ContainingTypes(type) + type.Name));
        var symbols = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
        string? fallback = null;
        Json.WriteStartObject();
        Json.WriteString("type", "enum");
        Json.WriteString("name", name);
        Json.WriteString("namespace", ns ?? string.Empty);
        WriteDoc(Constructor<string>(Attribute(type, DocAttribute)) ?? Summary(type));
        WriteAliases(type);
        Json.WriteStartArray("symbols");
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = Constructor<string>(Attribute(symbols[i], NameAttribute)) ?? symbols[i].Name;
            if (Convert.ToInt64(symbols[i].ConstantValue, CultureInfo.InvariantCulture) != i)
            {
                Report(SerializableTypeDiagnostics.EnumValues, $"{type.Name}.{symbols[i].Name} is {symbols[i].ConstantValue}; Avro enums are written by position, so the values must be 0, 1, 2 and so on, in declaration order.", symbols[i]);
            }

            if (!AvroNames.IsValidName(symbol.AsSpan()))
            {
                Report(SerializableTypeDiagnostics.InvalidName, $"'{symbol}' is not a valid Avro enum symbol; rename it with [AvroName].", symbols[i]);
            }

            if (Attribute(symbols[i], EnumDefaultAttribute) is not null)
            {
                fallback = symbol;
            }

            Json.WriteStringValue(symbol);
        }

        Json.WriteEndArray();
        if (fallback is not null)
        {
            Json.WriteString("default", fallback);
        }

        Json.WriteEndObject();
    }

    // --- Helpers ---

    private bool Define(string fullName, ISymbol symbol, ISymbol location)
    {
        if (_defined.TryGetValue(fullName, out var existing))
        {
            if (SymbolEqualityComparer.Default.Equals(existing, symbol))
            {
                Json.WriteStringValue(fullName);
                return false;
            }

            Report(SerializableTypeDiagnostics.NameCollision, $"The Avro name {fullName} is used by both {existing.ToDisplayString()} and {symbol.ToDisplayString()}; give one another name or namespace.", location);
            Json.WriteStringValue(fullName);
            return false;
        }

        _defined.Add(fullName, symbol);
        return true;
    }

    private void CheckName(string name, string? ns, ISymbol symbol)
    {
        if (!AvroNames.IsValidName(name.AsSpan()) || (ns is not null && !AvroNames.IsValidNamespace(ns.AsSpan())))
        {
            Report(SerializableTypeDiagnostics.InvalidName, $"'{(ns is null ? name : ns + "." + name)}' is not a valid Avro name; set another with the attribute's Name or Namespace.", symbol);
        }
    }

    private void WriteDoc(string? doc)
    {
        if (!string.IsNullOrWhiteSpace(doc))
        {
            Json.WriteString("doc", doc);
        }
    }

    private void WriteAliases(ISymbol symbol)
    {
        var aliases = symbol.GetAttributes().Where(a => Is(a, AliasAttribute)).Select(a => Constructor<string>(a)).OfType<string>().ToList();
        if (aliases.Count == 0)
        {
            return;
        }

        Json.WriteStartArray("aliases");
        foreach (var alias in aliases)
        {
            Json.WriteStringValue(alias);
        }

        Json.WriteEndArray();
    }

    /// <summary>The text of a symbol's XML <c>summary</c>, when the compilation has documentation comments.</summary>
    private string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: _cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var summary = XElement.Parse(xml).Element("summary")?.Value;
            return summary is null ? null : string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // The namespace of the record being written, which a fixed type defined in it takes by default.
    private static string? CurrentNamespace(ISymbol member) =>
        member.ContainingType is { } type && type.GetAttributes().FirstOrDefault(a => Is(a, AttributeName)) is { } attribute
            ? Named<string>(attribute, "Namespace") ?? Namespace(type)
            : null;

    private static ITypeSymbol MemberType(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => throw new InvalidOperationException("Not a property or field."),
    };

    private static string? Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    private static string ContainingTypes(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            names.Insert(0, current.Name);
        }

        return names.Count == 0 ? string.Empty : string.Join(".", names) + ".";
    }

    private static string Identifier(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static bool Is(AttributeData attribute, string fullName) =>
        string.Equals(attribute.AttributeClass?.ToDisplayString(), fullName, StringComparison.Ordinal);

    private static AttributeData? Attribute(ISymbol symbol, string fullName) => symbol.GetAttributes().FirstOrDefault(a => Is(a, fullName));

    private static T? Constructor<T>(AttributeData? attribute) =>
        attribute is { ConstructorArguments.Length: > 0 } && attribute.ConstructorArguments[0].Value is T value ? value : default;

    private static T? Named<T>(AttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return default;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (string.Equals(argument.Key, name, StringComparison.Ordinal) && argument.Value.Value is { } value)
            {
                return value is T typed ? typed : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
            }
        }

        return default;
    }

    private static DiagnosticInfo At(string id, string message, ISymbol symbol) => DiagnosticInfo.At(id, message, symbol.Locations.FirstOrDefault());

    private void Report(string id, string message, ISymbol symbol) => _diagnostics.Add(At(id, message, symbol));

    private sealed class RecordInfo(INamedTypeSymbol type, string fullName, List<ISymbol> members)
    {
        public INamedTypeSymbol Type { get; } = type;

        public string FullName { get; } = fullName;

        public List<ISymbol> Members { get; } = members;
    }

    /// <summary>A member's mapping attributes, and whether each was used, so one that doesn't apply is reported.</summary>
    private sealed class MemberAttributes(ISymbol member)
    {
        public ISymbol Member { get; } = member;

        public AttributeData? Decimal { get; } = Attribute(member, DecimalAttribute);

        public AttributeData? Fixed { get; } = Attribute(member, FixedAttribute);

        public AttributeData? LogicalType { get; } = Attribute(member, LogicalTypeAttribute);

        public bool DecimalUsed { get; set; }

        public bool FixedUsed { get; set; }

        public bool LogicalTypeUsed { get; set; }

        public void ReportUnused(SerializableTypeAnalyzer analyzer)
        {
            foreach (var (attribute, used, name) in new[] { (Decimal, DecimalUsed, "AvroDecimal"), (Fixed, FixedUsed, "AvroFixed"), (LogicalType, LogicalTypeUsed, "AvroLogicalType") })
            {
                if (attribute is not null && !used)
                {
                    analyzer.Report(SerializableTypeDiagnostics.MisappliedAttribute, $"[{name}] does not apply to {Member.Name}, a {MemberType(Member).ToDisplayString()}: remove it, or change the member's type.", Member);
                }
            }
        }
    }
}
