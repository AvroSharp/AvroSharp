using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>
/// Generates C# types for the named types (records, enums and fixed types) of Avro schemas. Each record gets static
/// <c>Write</c>/<c>Read</c> methods that call <see cref="AvroSharp.IO.AvroWriter"/>/<see cref="AvroSharp.IO.AvroReader"/> directly, in schema order.
/// </summary>
/// <remarks>Output is deterministic: the same schemas and options always produce the same files, in the same order.</remarks>
/// <seealso cref="CodeGenOptions"/>
/// <seealso cref="GeneratedSource"/>
/// <seealso cref="SchemaFileSet"/>
public static class CSharpCodeGenerator
{
    private const string Tool = "AvroSharp.CodeGen";

    // Records with more fields are serialized by several methods of at most this many fields each. One method per
    // record runs past the JIT's limit on tracked locals for very large records, after which it stops inlining the
    // reader and writer calls; chunks keep every method well under it.
    private const int FieldsPerMethod = 32;

    // Doc comment lines are wrapped at this many characters of text.
    private const int DocWidth = 110;

    // Schema JSON literals are split into lines of this many characters.
    private const int LiteralWidth = 100;

    /// <summary>Generates one source file per named type reachable from <paramref name="schemas"/>.</summary>
    /// <param name="schemas">The schemas. A named type reachable from several of them is generated once.</param>
    /// <param name="options">Options, or <see langword="null"/> for <see cref="CodeGenOptions.Default"/>.</param>
    public static IReadOnlyList<GeneratedSource> Generate(IEnumerable<AvroSchema> schemas, CodeGenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        options ??= CodeGenOptions.Default;
        ValidateNamespaceMapping(options);
        ValidateDefaultNamespace(options);
        ValidateLanguage(options);
        var names = new CSharpNames(options);
        var types = new TypeMapper(names, options);

        var named = new SortedDictionary<string, NamedSchema>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            Collect(schema, named);
        }

        var version = Version();
        var apache = options.ApacheCompatible;
        var renamed = AssignTypeNames(named.Values, names, apache);
        CheckCSharpNames(named.Values, names);
        var sources = named.Values.Select(schema =>
        {
            var notes = new List<string>();
            if (renamed.TryGetValue(schema.FullName, out var note))
            {
                notes.Add(note);
            }

            return new GeneratedSource(schema.FullName + ".g.cs", Emit(schema, names, types, version, apache, notes), notes) { Namespace = names.Namespace(schema) };
        }).ToList();
        if (apache)
        {
            sources.Add(new GeneratedSource(ApacheSupport.HintName, ApacheSupport.Source(version, options.NullableAnnotations)) { Namespace = ApacheSupport.Namespace });
        }

        return sources;
    }

    /// <summary>
    /// Generates the schema and serializers of records whose C# types the user declared (the attribute-driven
    /// generator's): one source per record, a <see langword="partial"/> declaration of its type.
    /// </summary>
    /// <param name="records">The records. Records and enums that they use must be in <paramref name="records"/> and <paramref name="enums"/>.</param>
    /// <param name="enums">The C# enums the records use.</param>
    /// <param name="options">Options; <see cref="CodeGenOptions.ByteArrayFixed"/> names the fixed types held as <c>byte[]</c>.</param>
    internal static IReadOnlyList<GeneratedSource> GenerateDeclared(IReadOnlyList<DeclaredRecord> records, IReadOnlyList<DeclaredEnum> enums, CodeGenOptions options)
    {
        ValidateLanguage(options);
        var (names, types) = DeclaredMapping(records, enums, options);
        var version = Version();
        return [.. records.Select(record => new GeneratedSource(
            record.Schema.FullName + ".g.cs",
            Emit(record.Schema, names, types, version, apache: false, [], record),
            []) { Namespace = record.Namespace })];
    }

    /// <summary>
    /// Gets the C# type that each field's member must have for its schema, as the serializers read and write it, so
    /// the attribute-driven generator can check the user's members before generating.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredFieldTypes(DeclaredRecord record, IReadOnlyList<DeclaredRecord> records, IReadOnlyList<DeclaredEnum> enums, CodeGenOptions options)
    {
        var (_, types) = DeclaredMapping(records, enums, options);
        return [.. record.Schema.Fields.Select(field => types.TypeOf(field.Schema))];
    }

    private static (CSharpNames Names, TypeMapper Types) DeclaredMapping(IReadOnlyList<DeclaredRecord> records, IReadOnlyList<DeclaredEnum> enums, CodeGenOptions options)
    {
        var names = new CSharpNames(options);
        foreach (var record in records)
        {
            names.SetDeclaredType(record.Schema.FullName, record.Namespace, record.Name);
        }

        foreach (var declared in enums)
        {
            names.SetDeclaredType(declared.AvroFullName, declared.Namespace, declared.Name);
        }

        return (names, new TypeMapper(names, options));
    }

    // The members the generator adds to each kind of type besides the fields' properties: a type may not have one
    // of these names (CS0542, #131).
    private static readonly string[] s_recordMembers =
    [
        "SchemaJson", "Schema", "AvroSharpSchema", "ApacheSchemaJson", "_SCHEMA", "s_schema", "s_apacheSchema", "s_plan",
        "Write", "Read", "WriteCore", "ReadCore", "ToAvroBytes", "TryWriteAvroBytes", "WriteAvroBytes", "WriteTo",
        "ReadFrom", "FromAvroBytes", "ReadResolved", "ReadField", "ReadPromoted", "ValueSerializer", "Get", "Put", "AvroTypeInfo",
        "s_typeInfo", "RegisterAvroType",
    ];

    private static readonly string[] s_fixedMembers =
    [
        "Size", "Value", "AsSpan", "Equals", "GetHashCode", "SchemaJson", "Schema", "AvroSharpSchema", "ApacheSchemaJson", "_SCHEMA", "s_schema",
        "s_apacheSchema",
    ];

    /// <summary>
    /// Renames a type whose name is also a member the generator adds to it (or, for an enum, one of its symbols), with
    /// a note for the type's source. In the Apache.Avro compatibility mode that is an error instead: Apache.Avro finds
    /// generated types by the schema's full name.
    /// </summary>
    private static Dictionary<string, string> AssignTypeNames(IEnumerable<NamedSchema> schemas, CSharpNames names, bool apache)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            // A type named var would also make the generated code's implicitly typed locals refer to it.
            var taken = new HashSet<string>(MembersOf(schema), StringComparer.Ordinal) { "var" };
            var name = schema.Name.Name;
            if (!taken.Contains(name))
            {
                continue;
            }

            var why = string.Equals(name, "var", StringComparison.Ordinal)
                ? "C# would read the generated code's implicitly typed locals as that type"
                : "C# does not allow a member named like its type, and " + name + " is " + (schema is EnumSchema ? "a symbol of the enum" : "a member the generator adds to it");
            if (apache)
            {
                throw new InvalidOperationException(
                    $"The type '{schema.FullName}' cannot be generated in the Apache.Avro compatibility mode: {why}. Rename the type in the schema.");
            }

            var candidate = name;
            for (var suffix = 1; taken.Contains(candidate); suffix++)
            {
                candidate = name + new string('_', suffix);
            }

            names.SetTypeName(schema, candidate);
            notes[schema.FullName] = $"Type '{schema.FullName}' is C# type {candidate}: {why}.";
        }

        return notes;
    }

    private static IEnumerable<string> MembersOf(NamedSchema schema)
    {
        switch (schema)
        {
            case RecordSchema record:
                var chunks = Chunks(record.Fields.Count).Count;
                return s_recordMembers
                    .Concat(Enumerable.Range(0, chunks).SelectMany(k => new[] { "WriteFields" + Int(k), "ReadFields" + Int(k), "ReadField" + Int(k) }))
                    .Concat(Enumerable.Range(0, record.Fields.Count).Select(i => "s_default" + Int(i)));
            case FixedSchema:
                return s_fixedMembers;
            case EnumSchema enumSchema:
                return enumSchema.Symbols;
            default:
                return [];
        }
    }

    /// <summary>
    /// Rejects C# names that collide after namespace mapping and renaming: two types with the same C# full name, or a
    /// type whose C# full name is also a namespace (CS0101, #131).
    /// </summary>
    private static void CheckCSharpNames(IEnumerable<NamedSchema> schemas, CSharpNames names)
    {
        var byName = new Dictionary<string, NamedSchema>(StringComparer.Ordinal);
        var namespaces = new Dictionary<string, NamedSchema>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            var fullName = names.TypeName(schema);
            if (byName.TryGetValue(fullName, out var other))
            {
                throw new InvalidOperationException(
                    $"The types '{other.FullName}' and '{schema.FullName}' are both the C# type {fullName["global::".Length..]}. Map their namespaces to different C# namespaces.");
            }

            byName.Add(fullName, schema);
            if (names.Namespace(schema) is { } ns)
            {
                // The namespace and each namespace that contains it.
                for (var end = ns.Length; end > 0; end = ns.LastIndexOf('.', end - 1))
                {
                    namespaces.TryAdd("global::" + ns[..end], schema);
                }
            }
        }

        foreach (var pair in byName)
        {
            if (namespaces.TryGetValue(pair.Key, out var inside))
            {
                throw new InvalidOperationException(
                    $"The type '{pair.Value.FullName}' is the C# type {pair.Key["global::".Length..]}, which is also the namespace of '{inside.FullName}'. " +
                    "C# does not allow a type and a namespace of the same name: rename the type, or map one of the namespaces.");
            }

            // A type named like a namespace or type the generated code refers to, or a prefix of one, hides it there (#161):
            // AvroSharp.Serialization, System.Collections, System.DateOnly.
            var name = pair.Key["global::".Length..];
            if (ExternalNames.FirstOrDefault(external => string.Equals(external, name, StringComparison.Ordinal) || external.StartsWith(name + ".", StringComparison.Ordinal)) is { } hidden)
            {
                throw new InvalidOperationException(
                    $"The type '{pair.Value.FullName}' is the C# type {name}, which would hide {hidden}, which the generated code uses. Map its namespace to another C# namespace.");
            }
        }
    }

    /// <summary>
    /// The namespaces and types outside the generated code that it refers to, by their full names. A test generates
    /// every construct and checks that the code refers to no other.
    /// </summary>
    internal static readonly string[] ExternalNames =
    [
        "Avro.AvroDecimal", "Avro.Schema", "Avro.Specific.ISpecificRecord", "Avro.Specific.SpecificFixed",
        "AvroSharp.AvroDataException", "AvroSharp.AvroException", "AvroSharp.Generated.ApacheDecimals",
        "AvroSharp.IO.AvroReader", "AvroSharp.IO.AvroWriter", "AvroSharp.Schemas.AvroSchema", "AvroSharp.Schemas.RecordSchema",
        "AvroSharp.Serialization.AvroLogicalValues", "AvroSharp.Serialization.IAvroSpecificRecord",
        "AvroSharp.Serialization.IAvroSerializable", "AvroSharp.Serialization.IAvroWritable", "AvroSharp.Serialization.IAvroReadable",

        // Every public type of the support namespace, as its API lists them: the value serializers are chosen by field type.
        "AvroSharp.Serialization.Generated.AvroBooleanSerializer", "AvroSharp.Serialization.Generated.AvroBytesSerializer",
        "AvroSharp.Serialization.Generated.AvroConversion", "AvroSharp.Serialization.Generated.AvroDoubleSerializer",
        "AvroSharp.Serialization.Generated.AvroFloatSerializer", "AvroSharp.Serialization.Generated.AvroGeneratedCode",
        "AvroSharp.Serialization.Generated.AvroIntSerializer", "AvroSharp.Serialization.Generated.AvroLongSerializer",
        "AvroSharp.Serialization.Generated.AvroPlanCache", "AvroSharp.Serialization.Generated.AvroRecordPlan",
        "AvroSharp.Serialization.Generated.AvroStringSerializer", "AvroSharp.Serialization.Generated.AvroUninitialized",
        "AvroSharp.Serialization.Generated.IAvroValueSerializer",
        "System.ArgumentNullException", "System.Array", "System.Buffers.IBufferWriter", "System.Buffers.ReadOnlySequence",
        "System.CodeDom.Compiler.GeneratedCode", "System.CodeDom.Compiler.GeneratedCodeAttribute",
        "System.Collections.Generic.Dictionary", "System.Collections.Generic.List",
        "System.ComponentModel.EditorBrowsable", "System.ComponentModel.EditorBrowsableState",
        "System.DateOnly", "System.DateTime", "System.DateTimeOffset", "System.Diagnostics.DebuggerDisplay",
        "System.Diagnostics.DebuggerNonUserCode", "System.Guid", "System.IEquatable", "System.MemoryExtensions",
        "System.Numerics.BigInteger", "System.ReadOnlySpan", "System.Runtime.CompilerServices.MethodImpl",
        "System.Runtime.CompilerServices.MethodImplOptions", "System.Runtime.InteropServices.CollectionsMarshal",
        "System.Span", "System.StringComparer", "System.TimeOnly", "System.TimeSpan",
    ];

    // Nullable annotations need C# 8: a contradiction is an error rather than code that does not compile.
    private static void ValidateLanguage(CodeGenOptions options)
    {
        if (options.LanguageVersion < 7)
        {
            throw new ArgumentException($"The language version {options.LanguageVersion.ToString(CultureInfo.InvariantCulture)} is below the lowest supported, C# 7 (7.2 or later: the generated code has readonly structs).", nameof(options));
        }

        if (options.NullableAnnotations && options.LanguageVersion < 8)
        {
            throw new ArgumentException($"Nullable annotations need C# 8 or later, and the language version is {options.LanguageVersion.ToString(CultureInfo.InvariantCulture)}: set NullableAnnotations to false.", nameof(options));
        }
    }

    private static void ValidateDefaultNamespace(CodeGenOptions options)
    {
        var ns = options.Namespace;
        if (!string.IsNullOrEmpty(ns) && !ns!.Split('.').All(part => AvroNames.IsValidName(part)))
        {
            throw new ArgumentException($"The namespace '{ns}' is not a C# namespace: names of letters, digits and underscores, separated by dots.", nameof(options));
        }
    }

    private static void ValidateNamespaceMapping(CodeGenOptions options)
    {
        if (options.NamespaceMap is not { Count: > 0 } mapping)
        {
            return;
        }

        if (options.ApacheCompatible)
        {
            throw new ArgumentException("A namespace mapping cannot be combined with the Apache.Avro compatibility mode: Apache.Avro finds generated types by the schema's full name.", nameof(options));
        }

        foreach (var pair in mapping)
        {
            if (!IsNamespace(pair.Key) || !IsNamespace(pair.Value))
            {
                throw new ArgumentException($"The namespace mapping '{pair.Key}' to '{pair.Value}' needs a namespace on each side: names separated by dots.", nameof(options));
            }
        }

        static bool IsNamespace(string value) => value.Length > 0 && value.Split('.').All(part => AvroNames.IsValidName(part));
    }

    // The version in [GeneratedCode]: the package version without build metadata (MinVer sets AssemblyVersion to
    // major.0.0.0, which is 0.0.0.0 for every 0.x release).
    private static string Version()
    {
        var assembly = typeof(CSharpCodeGenerator).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var version = informational!;
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    private static void Collect(AvroSchema schema, SortedDictionary<string, NamedSchema> named)
    {
        switch (schema)
        {
            case NamedSchema namedSchema when named.ContainsKey(namedSchema.FullName):
                return;
            case RecordSchema record:
                named.Add(record.FullName, record);
                foreach (var field in record.Fields)
                {
                    Collect(field.Schema, named);
                }

                break;
            case NamedSchema namedSchema:
                named.Add(namedSchema.FullName, namedSchema);
                break;
            case ArraySchema array:
                Collect(array.Items, named);
                break;
            case MapSchema map:
                Collect(map.Values, named);
                break;
            case UnionSchema union:
                foreach (var branch in union.Branches)
                {
                    Collect(branch, named);
                }

                break;
        }
    }

    private static string Emit(NamedSchema schema, CSharpNames names, TypeMapper types, string version, bool apache, List<string> notes, DeclaredRecord? declared = null)
    {
        var w = new CodeWriter();
        w.Line("// <auto-generated/>");
        w.Line($"// Generated by {Tool} from the Avro schema of '{string.Join(" ", CSharpNames.CommentLines(schema.FullName))}'. Do not edit.");
        if (types.Annotations)
        {
            w.Line("#nullable enable");
        }

        // CS1591: members without an Avro doc have no documentation comment. CS8981: Avro names may be all lower case.
        w.Line("#pragma warning disable CS1591, CS8981");
        w.Line();

        var ns = names.Namespace(schema);
        if (ns is not null)
        {
            w.Open($"namespace {ns}");
        }

        // A declared type keeps its own documentation and attributes: the generated part adds members only.
        if (declared is null)
        {
            Doc(w, schema.Doc);
            w.Line($"[global::System.CodeDom.Compiler.GeneratedCode({CSharpNames.Literal(Tool)}, {CSharpNames.Literal(version)})]");
        }

        var name = names.SimpleName(schema);
        switch (schema)
        {
            case RecordSchema record:
                EmitRecord(w, record, name, names, types, apache, notes, declared);
                break;
            case EnumSchema enumSchema:
                EmitEnum(w, enumSchema, name);
                break;
            case FixedSchema fixedSchema:
                EmitFixed(w, fixedSchema, name, apache, types);
                break;
        }

        if (ns is not null)
        {
            w.Close();
        }

        return w.ToString();
    }

    private const string Writer = "global::AvroSharp.IO.AvroWriter";
    private const string Reader = "global::AvroSharp.IO.AvroReader";
    private const string Support = "global::AvroSharp.Serialization.Generated.AvroGeneratedCode";
    private const string Serialization = "global::AvroSharp.Serialization";
    private const string Generated = "global::AvroSharp.Serialization.Generated";

    // Step-into from user code skips the serializers, which are thousands of generated lines for large schemas.
    private const string NonUserCode = "[global::System.Diagnostics.DebuggerNonUserCode]";

    private static void EmitRecord(CodeWriter w, RecordSchema record, string name, CSharpNames names, TypeMapper types, bool apache, List<string> notes, DeclaredRecord? declared)
    {
        var properties = declared is null ? PropertyNames(record, name, types.Naming, notes) : [.. declared.Members];

        // Apache's ISpecificRecord has an instance Schema (and avrogen a static _SCHEMA), so AvroSharp's static schema
        // is AvroSharpSchema in that mode, as on fixed types.
        var schemaProperty = apache ? "AvroSharpSchema" : "Schema";
        if (declared is null && DebuggerDisplay(record, properties, types) is { } display)
        {
            w.Line($"[global::System.Diagnostics.DebuggerDisplay({CSharpNames.Literal(display)})]");
        }

        // A declared type's other parts give its accessibility and modifiers, and a record class must say so here too.
        var declaration = declared is null ? "public sealed partial class" : declared.IsRecordClass ? "partial record" : "partial class";
        w.Line($"{declaration} {name} : {Serialization}.IAvroSpecificRecord, {Serialization}.IAvroWritable, {Serialization}.IAvroReadable{(apache ? ", " + ApacheSupport.SpecificRecord : string.Empty)}");
        if (types.Modern)
        {
            w.Directive("#if NET8_0_OR_GREATER");
            w.Line($"    , {Serialization}.IAvroSerializable<{name}>");
            w.Directive("#endif");
        }

        w.Open();
        EmitSchemaMembers(w, record, schemaProperty, apache, types);
        if (apache)
        {
            ApacheSupport.EmitRecordSchema(w, types);
        }

        if (declared is null)
        {
            EmitProperties(w, record, properties, types);
        }

        EmitConstructors(w, record, name, properties, types, declared is not null);
        EmitRecordApi(w, name);
        EmitTypeInfo(w, name, schemaProperty, types);
        EmitResolvingApi(w, name, schemaProperty, types);
        if (types.Modern && apache)
        {
            w.Line();
            w.Directive("#if NET8_0_OR_GREATER");
            w.Line($"static global::AvroSharp.Schemas.AvroSchema {Serialization}.IAvroSerializable<{name}>.Schema => AvroSharpSchema;");
            w.Directive("#endif");
        }

        EmitRecordAccess(w, record, properties, types, schemaProperty, apache);
        if (apache)
        {
            ApacheSupport.EmitRecordMembers(w, types);
        }

        EmitCodec(w, name);
        EmitRecordCore(w, record, name, properties, new SerializerEmitter(names, types), types);
        w.Close();
    }

    private static void EmitProperties(CodeWriter w, RecordSchema record, string[] properties, TypeMapper types)
    {
        for (var i = 0; i < record.Fields.Count; i++)
        {
            w.Line();
            Doc(w, record.Fields[i].Doc);
            if (RawLogicalRemarks(record.Fields[i].Schema, types) is { } remarks)
            {
                w.Line($"/// <remarks>{CSharpNames.Xml(remarks)}</remarks>");
            }

            w.Line($"public {types.TypeOf(record.Fields[i].Schema)} {properties[i]} {{ get; set; }}");
        }
    }

    // What a property holds when its logical type keeps the underlying type (raw mapping, or no .NET type for it), so
    // the meaning of the raw value is in its documentation.
    private static string? RawLogicalRemarks(AvroSchema schema, TypeMapper types)
    {
        if (schema is UnionSchema union && TypeMapper.Classify(union) is { NullIndex: >= 0, Others.Count: 1 } nullable)
        {
            schema = union.Branches[nullable.Others[0]];
        }

        if (schema.LogicalType is not { } logical || types.Logical(schema) is not null)
        {
            return null;
        }

        return logical.Kind switch
        {
            AvroLogicalTypeKind.Decimal when logical is DecimalLogicalType dec =>
                $"Avro decimal({dec.Precision.ToString(CultureInfo.InvariantCulture)},{dec.Scale.ToString(CultureInfo.InvariantCulture)}): the unscaled value as big-endian two's-complement bytes; the value is unscaled / 10^{dec.Scale.ToString(CultureInfo.InvariantCulture)}.",
            AvroLogicalTypeKind.Duration => "Avro duration: three little-endian unsigned 32-bit integers, months, days and milliseconds.",
            AvroLogicalTypeKind.Uuid => schema is FixedSchema ? "Avro uuid: the 16 bytes of the UUID in RFC 4122 order." : "Avro uuid: the UUID as a string.",
            AvroLogicalTypeKind.Date => "Avro date: days since 1970-01-01.",
            AvroLogicalTypeKind.TimeMillis => "Avro time-millis: milliseconds after midnight.",
            AvroLogicalTypeKind.TimeMicros => "Avro time-micros: microseconds after midnight.",
            AvroLogicalTypeKind.TimestampMillis => "Avro timestamp-millis: milliseconds since 1970-01-01T00:00:00Z.",
            AvroLogicalTypeKind.TimestampMicros => "Avro timestamp-micros: microseconds since 1970-01-01T00:00:00Z.",
            AvroLogicalTypeKind.TimestampNanos => "Avro timestamp-nanos: nanoseconds since 1970-01-01T00:00:00Z.",
            AvroLogicalTypeKind.LocalTimestampMillis => "Avro local-timestamp-millis: milliseconds since 1970-01-01T00:00:00 in an unspecified time zone.",
            AvroLogicalTypeKind.LocalTimestampMicros => "Avro local-timestamp-micros: microseconds since 1970-01-01T00:00:00 in an unspecified time zone.",
            AvroLogicalTypeKind.LocalTimestampNanos => "Avro local-timestamp-nanos: nanoseconds since 1970-01-01T00:00:00 in an unspecified time zone.",
            _ => $"Avro logical type {logical.Name}.",
        };
    }

    // What the debugger shows for a record: its first three fields that display as one value (numbers, strings, enums,
    // logical types, and nullable ones), for example "Id = {Id}, Name = {Name}", instead of only the type name.
    private static string? DebuggerDisplay(RecordSchema record, string[] properties, TypeMapper types)
    {
        var shown = new List<string>();
        for (var i = 0; i < record.Fields.Count && shown.Count < 3; i++)
        {
            var schema = record.Fields[i].Schema;
            if (schema is UnionSchema union && TypeMapper.Classify(union) is { NullIndex: >= 0, Others.Count: 1 } nullable)
            {
                schema = union.Branches[nullable.Others[0]];
            }

            var simple = types.Logical(schema) is not null
                || schema is EnumSchema
                || schema.Type is AvroSchemaType.Boolean or AvroSchemaType.Int or AvroSchemaType.Long or AvroSchemaType.Float or AvroSchemaType.Double or AvroSchemaType.String;
            if (simple)
            {
                shown.Add($"{properties[i].TrimStart('@')} = {{{properties[i]}}}");
            }
        }

        return shown.Count == 0 ? null : string.Join(", ", shown);
    }

    /// <summary>
    /// The public constructor gives each field its schema default, or a valid empty value when it has none; the
    /// readers' constructor skips all of it, since a reader sets every field (so collections are not allocated twice).
    /// </summary>
    private static void EmitConstructors(CodeWriter w, RecordSchema record, string name, string[] properties, TypeMapper types, bool declared)
    {
        if (declared)
        {
            // The user's type has its own constructors and initializers; readers need only theirs.
            EmitUninitializedConstructor(w, name, types);
            return;
        }

        w.Line();
        w.Line("/// <summary>Creates a value in which every field with a default in the schema has it.</summary>");
        w.Open($"public {name}()");
        for (var i = 0; i < record.Fields.Count; i++)
        {
            var field = record.Fields[i];
            if (types.DefaultValue(field) is { } value)
            {
                w.Line($"{properties[i]} = {value};");
            }
            else if (types.Initializer(field.Schema) is { } initializer)
            {
                w.Line($"{properties[i]} = {initializer};");
            }
        }

        // Defaults with no C# literal (records, fixed values, logical types, collections of them) are decoded from
        // their Avro encoding by the field's reader, as reading data without the field does.
        var encoded = new List<(int Index, byte[] Bytes)>();
        for (var i = 0; i < record.Fields.Count; i++)
        {
            if (types.DefaultBytes(record.Fields[i]) is { } bytes)
            {
                encoded.Add((i, bytes));
            }
        }

        foreach (var (index, _) in encoded)
        {
            w.Open();
            w.Line($"var reader = new {Reader}(s_default{Int(index)});");
            w.Line($"ReadField(ref reader, this, {Int(index)}, 0);");
            w.Close();
        }

        w.Close();

        foreach (var (index, bytes) in encoded)
        {
            w.Line();
            w.Line($"// The Avro encoding of {CSharpNames.Literal(record.Fields[index].Name)}'s default.");
            w.Line($"private static global::System.ReadOnlySpan<byte> s_default{Int(index)} => new byte[] {{ {string.Join(", ", bytes.Select(b => "0x" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))} }};");
        }

        EmitUninitializedConstructor(w, name, types);
    }

    /// <summary>The constructor readers use: it sets nothing, since they set every field.</summary>
    private static void EmitUninitializedConstructor(CodeWriter w, string name, TypeMapper types)
    {
        w.Line();
        if (types.Annotations)
        {
            w.Directive("#pragma warning disable CS8618 // Readers set every field.");
        }

        w.Open($"private {name}({Generated}.AvroUninitialized _)");
        w.Close();
        if (types.Annotations)
        {
            w.Directive("#pragma warning restore CS8618");
        }
    }

    /// <summary>
    /// <c>AvroTypeInfo</c>, the type's registration in <c>AvroTypes</c>, which a module initializer adds on targets
    /// that have them (.NET 5 and later); elsewhere code calls <c>AvroTypes.Register</c> with it.
    /// </summary>
    private static void EmitTypeInfo(CodeWriter w, string name, string schemaProperty, TypeMapper types)
    {
        var info = $"{Serialization}.AvroTypeInfo<{name}>";
        w.Line();
        w.Line($"private static {types.Nullable(info)} s_typeInfo;");
        w.Line();
        w.Line("/// <summary>Gets this type's schema and serializers, as <c>AvroTypes</c> finds them by type.</summary>");
        w.Line($"public static {info} AvroTypeInfo => s_typeInfo ?? (s_typeInfo = new {info}(() => {schemaProperty}, Write, Read, writerSchema => writerSchema.HasSameCanonicalForm({schemaProperty}) ? new {Serialization}.AvroReadFunc<{name}>(Read) : (ref {Reader} reader) => Read(ref reader, writerSchema)));");
        if (types.LanguageVersion < 9)
        {
            return;
        }

        // Module initializers need C# 9.
        w.Line();
        w.Directive("#if NET5_0_OR_GREATER");
        w.Line("// CA2255: generated code registers its types; that's the attribute's purpose here.");
        w.Directive("#pragma warning disable CA2255");
        w.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        w.Line($"internal static void RegisterAvroType() => {Serialization}.AvroTypes.Register(AvroTypeInfo);");
        w.Directive("#pragma warning restore CA2255");
        w.Directive("#endif");
    }

    /// <summary>The public entry points; they delegate to the depth-tracking cores.</summary>
    private static void EmitRecordApi(CodeWriter w, string name)
    {
        w.Line();
        w.Line("/// <summary>Writes a value in Avro binary encoding.</summary>");
        w.Open($"public static void Write(ref {Writer} writer, {name} value)");
        w.Open("if (value is null)");
        // A literal, not nameof(value): a namespace named nameof would capture the operator (#131).
        w.Line("throw new global::System.ArgumentNullException(\"value\");");
        w.Close();
        w.Line();
        w.Line("WriteCore(ref writer, value, 0);");
        w.Close();

        w.Line();
        w.Line("/// <summary>Reads a value written with this type's schema.</summary>");
        w.Open($"public static {name} Read(ref {Reader} reader)");
        w.Line($"{Support}.BeginRead(ref reader);");
        w.Line("return ReadCore(ref reader, null, 0);");
        w.Close();

        w.Line();
        w.Line("/// <summary>Writes this value to a new array, in Avro binary encoding.</summary>");
        w.Line($"public byte[] ToAvroBytes() => {Support}.SerializeToArray(this, Write);");

        EmitMemoryApi(w);
        EmitFromBytes(w, name);
    }

    /// <summary>Writing into caller memory and reuse of instances (<c>IAvroWritable</c>, <c>IAvroReadable</c>).</summary>
    private static void EmitMemoryApi(CodeWriter w)
    {
        w.Line();
        w.Line("/// <summary>Writes this value into <paramref name=\"destination\"/>, allocating nothing.</summary>");
        w.Line("/// <returns><see langword=\"true\"/> when it fit; otherwise <paramref name=\"bytesWritten\"/> is 0.</returns>");
        w.Open("public bool TryWriteAvroBytes(global::System.Span<byte> destination, out int bytesWritten)");
        w.Line($"var writer = {Support}.BeginTryWrite(destination);");
        w.Line("WriteCore(ref writer, this, 0);");
        w.Line($"return {Support}.EndTryWrite(ref writer, out bytesWritten);");
        w.Close();

        w.Line();
        w.Line("/// <summary>Writes this value into <paramref name=\"destination\"/> and returns the bytes written; throws when it does not fit.</summary>");
        w.Open("public int WriteAvroBytes(global::System.Span<byte> destination)");
        w.Line($"var writer = new {Writer}(destination);");
        w.Line("WriteCore(ref writer, this, 0);");
        w.Line("return (int)writer.BytesWritten;");
        w.Close();

        w.Line();
        w.Line("/// <summary>Writes this value to <paramref name=\"output\"/>, allocating nothing for a reused buffer writer.</summary>");
        w.Open("public void WriteAvroBytes(global::System.Buffers.IBufferWriter<byte> output)");
        w.Line($"var writer = new {Writer}(output);");
        w.Line("WriteCore(ref writer, this, 0);");
        w.Line("writer.Flush();");
        w.Close();

        w.Line();
        w.Line("/// <summary>Writes this value (<c>IAvroWritable</c>).</summary>");
        w.Line($"public void WriteTo(ref {Writer} writer) => WriteCore(ref writer, this, 0);");

        w.Line();
        w.Line("/// <summary>Reads a value into this instance, filling its lists, dictionaries and records again (<c>IAvroReadable</c>).</summary>");
        w.Open($"public void ReadFrom(ref {Reader} reader)");
        w.Line($"{Support}.BeginRead(ref reader);");
        w.Line("ReadCore(ref reader, this, 0);");
        w.Close();
    }

    /// <summary>Reading a value from memory: a span, a span that holds more, or a sequence of buffers.</summary>
    private static void EmitFromBytes(CodeWriter w, string name)
    {
        w.Line();
        w.Line("/// <summary>Reads a value from Avro binary data written with this type's schema.</summary>");
        w.Open($"public static {name} FromAvroBytes(global::System.ReadOnlySpan<byte> data)");
        w.Line($"var reader = new {Reader}(data);");
        w.Line("return ReadCore(ref reader, null, 0);");
        w.Close();

        w.Line();
        w.Line("/// <summary>Reads a value from the start of <paramref name=\"data\"/> and reports how many bytes it took.</summary>");
        w.Open($"public static {name} FromAvroBytes(global::System.ReadOnlySpan<byte> data, out int bytesConsumed)");
        w.Line($"var reader = new {Reader}(data);");
        w.Line("var value = ReadCore(ref reader, null, 0);");
        w.Line("bytesConsumed = (int)reader.BytesConsumed;");
        w.Line("return value;");
        w.Close();

        w.Line();
        w.Line("/// <summary>Reads a value from a sequence of buffers, such as a <c>PipeReader</c> result.</summary>");
        w.Open($"public static {name} FromAvroBytes(in global::System.Buffers.ReadOnlySequence<byte> data)");
        w.Line($"var reader = new {Reader}(data);");
        w.Line("return ReadCore(ref reader, null, 0);");
        w.Close();
    }

    /// <summary>
    /// Schema resolution: data of another version of the schema is read by the pair's plan (the last one is cached per
    /// type), or resolved to this type's schema first when the writer schema is not a record of the same name.
    /// </summary>
    private static void EmitResolvingApi(CodeWriter w, string name, string schemaProperty, TypeMapper types)
    {
        w.Line();
        w.Line($"private static {types.Nullable($"{Generated}.AvroPlanCache")} s_plan;");
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Reads a value written with <paramref name=\"writerSchema\"/>, another version of this type's schema, resolving the");
        w.Line("/// differences as the specification allows (added fields take their defaults, removed fields are skipped, numbers");
        w.Line("/// are promoted). Data written with this type's own schema is read directly.");
        w.Line("/// </summary>");
        w.Open($"public static {name} Read(ref {Reader} reader, global::AvroSharp.Schemas.AvroSchema writerSchema)");
        w.Line($"{Support}.BeginRead(ref reader);");
        w.Open($"if ((writerSchema ?? throw new global::System.ArgumentNullException(\"writerSchema\")).HasSameCanonicalForm({schemaProperty}))");
        w.Line("return ReadCore(ref reader, null, 0);");
        w.Close();
        w.Line();
        w.Line($"var plan = {Support}.GetRecordPlan(writerSchema, {schemaProperty}, ref s_plan);");
        w.Open("if (plan != null)");
        w.Line("return ReadResolved(ref reader, plan, 0);");
        w.Close();
        w.Line();
        w.Line($"return FromAvroBytes({Support}.ResolveToReaderEncoding(ref reader, writerSchema, {schemaProperty}));");
        w.Close();

        w.Line();
        w.Line("/// <summary>Reads a value from Avro binary data written with <paramref name=\"writerSchema\"/>, resolving schema differences.</summary>");
        w.Open($"public static {name} FromAvroBytes(global::System.ReadOnlySpan<byte> data, global::AvroSharp.Schemas.AvroSchema writerSchema)");
        w.Line($"var reader = new {Reader}(data);");
        w.Line("return Read(ref reader, writerSchema);");
        w.Close();
    }

    /// <summary>
    /// <c>IAvroSpecificRecord</c>: field access by position, like Apache.Avro's <c>ISpecificRecord</c>. <c>Put</c> checks
    /// the value's type and names the field in its error. Uses only C# 7.3 syntax.
    /// </summary>
    private static void EmitRecordAccess(CodeWriter w, RecordSchema record, string[] properties, TypeMapper types, string schemaProperty, bool apache)
    {
        const string Interface = "global::AvroSharp.Serialization.IAvroSpecificRecord";
        var count = record.Fields.Count.ToString(CultureInfo.InvariantCulture);
        var invalid = $"throw {Support}.InvalidFieldPosition(fieldPos, {count}, {CSharpNames.Literal(record.FullName)});";

        w.Line();
        w.Line($"global::AvroSharp.Schemas.RecordSchema {Interface}.Schema => (global::AvroSharp.Schemas.RecordSchema){schemaProperty};");

        w.Line();
        w.Line("/// <summary>Gets the value of the field at a position in the schema, boxed.</summary>");
        w.Open($"public {types.Nullable("object")} Get(int fieldPos)");
        w.Open("switch (fieldPos)");
        for (var i = 0; i < properties.Length; i++)
        {
            w.Line($"case {Int(i)}:");
            w.Indent();
            w.Line($"return this.{properties[i]};");
            w.Outdent();
        }

        w.Line("default:");
        w.Indent();
        w.Line(invalid);
        w.Outdent();
        w.Close();
        w.Close();

        EmitPut(w, record, properties, types, schemaProperty, apache, invalid);
    }

    private static void EmitPut(CodeWriter w, RecordSchema record, string[] properties, TypeMapper types, string schemaProperty, bool apache, string invalid)
    {
        w.Line();
        w.Line("/// <summary>Sets the value of the field at a position in the schema; the value must have the field's C# type.</summary>");
        w.Open($"public void Put(int fieldPos, {types.Nullable("object")} fieldValue)");
        w.Open("switch (fieldPos)");
        for (var i = 0; i < properties.Length; i++)
        {
            w.Line($"case {Int(i)}:");
            w.Indent();
            var schema = record.Fields[i].Schema;
            if (schema.Type == AvroSchemaType.Null)
            {
                // The field always holds null; any other value would be dropped when writing.
                w.Open("if (fieldValue != null)");
                w.Line($"throw {Support}.PutTypeMismatch(fieldValue, {schemaProperty}, {Int(i)}, \"null\");");
                w.Close();
            }
            else
            {
                w.Line($"this.{properties[i]} = {PutValue(schema, types, i, "v" + Int(i), schemaProperty, apache)};");
            }

            w.Line("break;");
            w.Outdent();
        }

        w.Line("default:");
        w.Indent();
        w.Line(invalid);
        w.Outdent();
        w.Close();
        w.Close();
    }

    /// <summary>The expression that converts <c>fieldValue</c> to a field's type, or throws naming the field.</summary>
    private static string PutValue(AvroSchema schema, TypeMapper types, int position, string variable, string schemaProperty, bool apache)
    {
        var nullable = false;
        if (schema is UnionSchema union)
        {
            var (nullIndex, others) = TypeMapper.Classify(union);
            if (others.Count != 1)
            {
                // object?: any value; the union branch is checked when writing.
                return "fieldValue";
            }

            nullable = nullIndex >= 0;
            schema = union.Branches[others[0]];
        }

        var type = types.TypeOf(schema);
        var display = type.ReplaceOrdinal("global::", string.Empty) + (nullable ? "?" : string.Empty);
        var mismatch = $"throw {Support}.PutTypeMismatch(fieldValue, {schemaProperty}, {Int(position)}, {CSharpNames.Literal(display)})";
        var converted = types.IsValueType(schema)
            ? $"fieldValue is {type} {variable} ? {variable} : {mismatch}"
            : $"fieldValue as {type} ?? {mismatch}";
        if (apache && schema is EnumSchema)
        {
            // Apache's specific reader puts an enum as its ordinal (a boxed int); its own generated code unboxes it.
            converted = $"fieldValue is {type} {variable} ? {variable} : fieldValue is int {variable}o ? ({type}){variable}o : {mismatch}";
        }
        else if (apache && schema is FixedSchema { LogicalType: DecimalLogicalType dec } fixedDecimal)
        {
            // Apache's specific reader puts a decimal on fixed as an AvroDecimal, after reusing the current value as a
            // SpecificFixed. Accept both; the field keeps the fixed type.
            var scale = dec.Scale.ToString(CultureInfo.InvariantCulture);
            var size = fixedDecimal.Size.ToString(CultureInfo.InvariantCulture);
            converted = $"fieldValue is {type} {variable} ? {variable} : fieldValue is global::Avro.AvroDecimal {variable}d ? new {type}(global::AvroSharp.Generated.ApacheDecimals.FixedBytes({variable}d, {scale}, {size})) : {mismatch}";
        }

        return nullable
            ? $"fieldValue == null ? ({(types.IsValueType(schema) ? type + "?" : types.Nullable(type))})null : {converted}"
            : converted;
    }

    /// <summary>The struct codec that the collection and union helpers read and write this record with.</summary>
    private static void EmitCodec(CodeWriter w, string name)
    {
        w.Line();
        w.Line("/// <summary>Reads and writes this type for the collection and union helpers of <c>AvroGeneratedCode</c>.</summary>");
        w.Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        w.Open($"internal readonly struct ValueSerializer : {Generated}.IAvroValueSerializer<{name}>");
        w.Line($"public {name} Read(ref {Reader} reader, int depth) => ReadCore(ref reader, null, depth);");
        w.Line();
        w.Line($"public void Write(ref {Writer} writer, {name} value, int depth) => WriteCore(ref writer, value, depth);");
        w.Close();
    }

    /// <summary>
    /// The serializers: each field in schema order, with the record depth checked on entry. Records with more than
    /// <see cref="FieldsPerMethod"/> fields are split into chunk methods.
    /// </summary>
    private static void EmitRecordCore(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, TypeMapper types)
    {
        var chunks = Chunks(record.Fields.Count);
        EmitWriteCore(w, record, name, properties, emitter, chunks);
        EmitReadCore(w, record, name, properties, emitter, types, chunks);
        EmitReadResolved(w, record, name, properties, emitter, chunks);
    }

    private static void EmitWriteCore(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, List<(int Start, int End)> chunks)
    {
        w.Line();
        w.Line(NonUserCode);
        w.Open($"internal static void WriteCore(ref {Writer} writer, {name} value, int depth)");
        w.Open($"if (depth > {Support}.MaxDepth)");
        w.Line($"throw {Support}.WriteTooDeep();");
        w.Close();
        if (chunks.Count == 1)
        {
            WriteFields(w, record, properties, emitter, 0, record.Fields.Count);
            w.Close();
        }
        else
        {
            w.Line();
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line($"WriteFields{Int(k)}(ref writer, value, depth);");
            }

            w.Close();
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line();
                w.Line(NonUserCode);
                w.Open($"private static void WriteFields{Int(k)}(ref {Writer} writer, {name} value, int depth)");
                WriteFields(w, record, properties, emitter, chunks[k].Start, chunks[k].End);
                w.Close();
            }
        }
    }

    private static void EmitReadCore(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, TypeMapper types, List<(int Start, int End)> chunks)
    {
        w.Line();
        w.Line("/// <summary>Reads a value into <paramref name=\"reuse\"/> (its collections and records are filled again), or into a new instance.</summary>");
        w.Line(NonUserCode);
        w.Open($"internal static {name} ReadCore(ref {Reader} reader, {types.Nullable(name)} reuse, int depth)");
        w.Open($"if (depth > {Support}.MaxDepth)");
        w.Line($"throw {Support}.ReadTooDeep();");
        w.Close();
        w.Line();
        w.Line($"var value = reuse ?? new {name}(default({Generated}.AvroUninitialized));");
        if (chunks.Count == 1)
        {
            ReadFields(w, record, properties, emitter, 0, record.Fields.Count);
        }
        else
        {
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line($"ReadFields{Int(k)}(ref reader, value, depth);");
            }
        }

        w.Line("return value;");
        w.Close();
        if (chunks.Count > 1)
        {
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line();
                w.Line(NonUserCode);
                w.Open($"private static void ReadFields{Int(k)}(ref {Reader} reader, {name} value, int depth)");
                ReadFields(w, record, properties, emitter, chunks[k].Start, chunks[k].End);
                w.Close();
            }
        }
    }

    private static void WriteFields(CodeWriter w, RecordSchema record, string[] properties, SerializerEmitter emitter, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            w.Line();
            emitter.Write(w, record.Fields[i].Schema, "value." + properties[i], record.FullName + "." + record.Fields[i].Name);
        }
    }

    private static void ReadFields(CodeWriter w, RecordSchema record, string[] properties, SerializerEmitter emitter, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            emitter.Read(w, record.Fields[i].Schema, "value." + properties[i], reuse: true);
        }
    }

    private static List<(int Start, int End)> Chunks(int count)
    {
        var chunks = new List<(int Start, int End)>();
        for (var start = 0; start < count || chunks.Count == 0; start += FieldsPerMethod)
        {
            chunks.Add((start, Math.Min(start + FieldsPerMethod, count)));
        }

        return chunks;
    }

    /// <summary>
    /// Reading data of another schema version by its plan: writer fields in the writer's order, each read into its
    /// reader property directly (<c>ReadField</c>), promoted or remapped (<c>ReadPromoted</c>) or, for other
    /// differences, transcoded and then read; the reader fields the writer lacks take their defaults.
    /// </summary>
    private static void EmitReadResolved(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, List<(int Start, int End)> chunks)
    {
        var promotions = Enumerable.Range(0, record.Fields.Count)
            .Select(i => (Field: i, Conversions: emitter.ResolvedConversions(record.Fields[i].Schema)))
            .Where(p => p.Conversions.Count > 0)
            .ToList();

        w.Line();
        w.Line(NonUserCode);
        w.Open($"internal static {name} ReadResolved(ref {Reader} reader, global::AvroSharp.Serialization.Generated.AvroRecordPlan plan, int depth)");
        w.Open($"if (depth > {Support}.MaxDepth)");
        w.Line($"throw {Support}.ReadTooDeep();");
        w.Close();
        w.Line();
        w.Line($"var value = new {name}(default({Generated}.AvroUninitialized));");
        w.Open("for (var step = 0; step < plan.StepCount; step++)");
        if (record.Fields.Count == 0)
        {
            // Nothing to fill: skip whatever fields the writer has.
            w.Line("plan.Skip(step, ref reader);");
            w.Close();
            w.Line();
            w.Line("return value;");
            w.Close();
            return;
        }

        EmitResolvedStep(w, promotions.Count > 0);
        w.Close();
        w.Line();
        w.Open("for (var index = 0; index < plan.DefaultCount; index++)");
        w.Line("var field = plan.DefaultReader(index);");
        w.Line("ReadField(ref field, value, plan.DefaultTarget(index), depth);");
        w.Close();
        w.Line();
        w.Line("return value;");
        w.Close();

        EmitReadField(w, record, name, properties, emitter, chunks);
        if (promotions.Count > 0)
        {
            EmitReadPromoted(w, record, name, properties, emitter, promotions);
        }
    }

    // The body of ReadResolved's loop: skip, read directly, convert, or transcode one writer field.
    private static void EmitResolvedStep(CodeWriter w, bool hasPromotions)
    {
        const string Conversion = "global::AvroSharp.Serialization.Generated.AvroConversion";
        w.Line("var target = plan.Target(step, out var conversion);");
        w.Open("if (target < 0)");
        w.Line("plan.Skip(step, ref reader);");
        w.Close();
        w.Open($"else if (conversion == {Conversion}.None)");
        w.Line("ReadField(ref reader, value, target, depth);");
        w.Close();
        w.Open(hasPromotions ? "else if (!ReadPromoted(ref reader, value, target, conversion, plan, step))" : "else");
        w.Line($"var field = new {Reader}(plan.Transcode(step, ref reader));");
        w.Line("ReadField(ref field, value, target, depth);");
        w.Close();
    }

    // One case per field: the same statements as ReadCore's, for direct, transcoded and default values.
    private static void EmitReadField(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, List<(int Start, int End)> chunks)
    {
        w.Line();
        w.Line(NonUserCode);
        w.Open($"private static void ReadField(ref {Reader} reader, {name} value, int field, int depth)");
        if (chunks.Count == 1)
        {
            ReadFieldCases(w, record, properties, emitter, 0, record.Fields.Count);
            w.Close();
        }
        else
        {
            w.Open($"switch (field / {Int(FieldsPerMethod)})");
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line($"case {Int(k)}:");
                w.Indent();
                w.Line($"ReadField{Int(k)}(ref reader, value, field, depth);");
                w.Line("break;");
                w.Outdent();
            }

            w.Close();
            w.Close();
            for (var k = 0; k < chunks.Count; k++)
            {
                w.Line();
                w.Line(NonUserCode);
                w.Open($"private static void ReadField{Int(k)}(ref {Reader} reader, {name} value, int field, int depth)");
                ReadFieldCases(w, record, properties, emitter, chunks[k].Start, chunks[k].End);
                w.Close();
            }
        }
    }

    // The conversions generated code performs itself: numeric promotions and enum remapping.
    private static void EmitReadPromoted(CodeWriter w, RecordSchema record, string name, string[] properties, SerializerEmitter emitter, List<(int Field, IReadOnlyList<string> Conversions)> promotions)
    {
        const string Conversion = "global::AvroSharp.Serialization.Generated.AvroConversion";
        w.Line();

        // Only the fields with numeric promotions or enum remapping are here, so it is small enough to inline into
        // ReadResolved's loop, as the conversions were before the loop called a method per field.
        w.Line("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        w.Line(NonUserCode);
        w.Open($"private static bool ReadPromoted(ref {Reader} reader, {name} value, int field, {Conversion} conversion, global::AvroSharp.Serialization.Generated.AvroRecordPlan plan, int step)");
        w.Open("switch (field)");
        foreach (var (field, conversions) in promotions)
        {
            w.Line($"case {Int(field)}:");
            w.Indent();
            w.Open("switch (conversion)");
            foreach (var conversion in conversions)
            {
                w.Line($"case {Conversion}.{conversion}:");
                w.Indent();
                emitter.ReadConverted(w, record.Fields[field].Schema, conversion, "value." + properties[field]);
                w.Line("return true;");
                w.Outdent();
            }

            w.Close();
            w.Line("break;");
            w.Outdent();
        }

        w.Close();
        w.Line();
        w.Line("return false;");
        w.Close();
    }

    private static void ReadFieldCases(CodeWriter w, RecordSchema record, string[] properties, SerializerEmitter emitter, int start, int end)
    {
        w.Open("switch (field)");
        for (var i = start; i < end; i++)
        {
            w.Line($"case {Int(i)}:");
            w.Indent();
            emitter.Read(w, record.Fields[i].Schema, "value." + properties[i]);
            w.Line("break;");
            w.Outdent();
        }

        w.Close();
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void EmitEnum(CodeWriter w, EnumSchema schema, string name)
    {
        w.Open($"public enum {name}");
        for (var i = 0; i < schema.Symbols.Count; i++)
        {
            w.Line($"{CSharpNames.Identifier(schema.Symbols[i])} = {i.ToString(CultureInfo.InvariantCulture)},");
        }

        w.Close();
    }

    private static void EmitFixed(CodeWriter w, FixedSchema schema, string name, bool apache, TypeMapper types)
    {
        if (apache)
        {
            ApacheSupport.EmitFixed(w, schema, name, types, s => EmitSchemaMembers(w, s, "AvroSharpSchema", apache: true, types));
            return;
        }

        w.Open($"public sealed partial class {name} : global::System.IEquatable<{name}>");
        w.Line("/// <summary>The number of bytes in a value.</summary>");
        w.Line($"public const int Size = {schema.Size.ToString(CultureInfo.InvariantCulture)};");
        w.Line();
        EmitSchemaMembers(w, schema, "Schema", apache: false, types);
        w.Line();
        w.Line("/// <summary>Creates a value from exactly <see cref=\"Size\"/> bytes. The array is not copied.</summary>");
        w.Open($"public {name}(byte[] value)");
        w.Line($"Value = global::AvroSharp.Serialization.Generated.AvroGeneratedCode.CheckFixedSize(value, Size, {CSharpNames.Literal(schema.FullName)});");
        w.Close();
        w.Line();
        w.Line("/// <summary>Gets the bytes.</summary>");
        w.Line("public byte[] Value { get; }");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line($"public bool Equals({types.Nullable(name)} other) => !(other is null) && global::System.MemoryExtensions.SequenceEqual(new global::System.ReadOnlySpan<byte>(Value), other.Value);");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line($"public override bool Equals({types.Nullable("object")} obj) => obj is {name} other && Equals(other);");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Open("public override int GetHashCode()");
        w.Line("var hash = -2128831035;");
        w.Open("foreach (var b in Value)");
        w.Line("hash = (hash ^ b) * 16777619;");
        w.Close();
        w.Line();
        w.Line("return hash;");
        w.Close();
        EmitFixedOperators(w, name, types);
        w.Close();
    }

    /// <summary>A fixed type's bytes as a read-only span, and <c>==</c>/<c>!=</c> by value, as its <c>Equals</c>. C# 7.3 syntax.</summary>
    internal static void EmitFixedOperators(CodeWriter w, string name, TypeMapper types)
    {
        w.Line();
        w.Line("/// <summary>Gets the bytes as a read-only span.</summary>");
        w.Line("public global::System.ReadOnlySpan<byte> AsSpan() => Value;");
        w.Line();
        w.Line("/// <summary>Compares two values by their bytes.</summary>");
        w.Line($"public static bool operator ==({types.Nullable(name)} left, {types.Nullable(name)} right) => left is null ? right is null : left.Equals(right);");
        w.Line();
        w.Line("/// <summary>Compares two values by their bytes.</summary>");
        w.Line($"public static bool operator !=({types.Nullable(name)} left, {types.Nullable(name)} right) => !(left == right);");
    }

    /// <summary>
    /// The schema JSON as a <c>const</c>, one literal for every target and C# version, and a lazily parsed
    /// <c>AvroSchema</c> property named <paramref name="propertyName"/>. With <paramref name="apache"/>, also the JSON
    /// that Apache.Avro can parse (see <see cref="ApacheSupport.SchemaJson"/>).
    /// </summary>
    /// <remarks>
    /// On .NET 8 and later the JSON was a UTF-8 literal, half the size in the assembly and parsed without a UTF-16 copy,
    /// but then <c>SchemaJson</c> could not be a <c>const</c>, and the source held the schema twice (#114). Parsing
    /// happens once per type, so the copy does not matter.
    /// </remarks>
    private static void EmitSchemaMembers(CodeWriter w, NamedSchema schema, string propertyName, bool apache, TypeMapper types)
    {
        const string AvroSchema = "global::AvroSharp.Schemas.AvroSchema";
        var json = schema.ToJson();
        w.Line($"private static {types.Nullable(AvroSchema)} s_schema;");
        w.Line();
        w.Line("/// <summary>The Avro schema of this type, as JSON.</summary>");
        w.Line("public const string SchemaJson =");
        SplitLiteral(w, json);
        w.Line();
        w.Line("/// <summary>Gets the Avro schema of this type.</summary>");
        w.Line($"public static {AvroSchema} {propertyName} => s_schema ?? {Support}.PublishSchema(ref s_schema, {AvroSchema}.Parse(SchemaJson));");

        if (apache)
        {
            var apacheJson = ApacheSupport.SchemaJson(json);
            w.Line();
            w.Line("/// <summary>The schema as Apache.Avro parses it (it rejects uuid on fixed, which the specification allows).</summary>");
            if (string.Equals(apacheJson, json, StringComparison.Ordinal))
            {
                w.Line("internal const string ApacheSchemaJson = SchemaJson;");
            }
            else
            {
                w.Line("internal const string ApacheSchemaJson =");
                SplitLiteral(w, apacheJson);
            }
        }
    }

    // A long string as concatenated literals of LiteralWidth characters (the compiler joins them, still a constant),
    // one per line.
    private static void SplitLiteral(CodeWriter w, string text)
    {
        var parts = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(LiteralWidth, text.Length - start);

            // Never split a surrogate pair.
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            parts.Add(text.Substring(start, length));
            start += length;
        }

        if (parts.Count == 0)
        {
            parts.Add(string.Empty);
        }

        w.Indent();
        for (var i = 0; i < parts.Count; i++)
        {
            w.Line(CSharpNames.Literal(parts[i]) + (i == parts.Count - 1 ? ";" : " +"));
        }

        w.Outdent();
    }

    /// <summary>
    /// PascalCase property names, made unique: a name may not repeat the type's name (CS0542), a generated member, or
    /// another field's name after case conversion.
    /// </summary>
    private static string[] PropertyNames(RecordSchema record, string typeName, PropertyNaming naming, List<string> notes)
    {
        var used = new HashSet<string>(StringComparer.Ordinal)
        {
            typeName.TrimStart('@'), "SchemaJson", "Schema", "AvroSharpSchema", "ApacheSchemaJson", "_SCHEMA",
            "s_schema", "s_apacheSchema", "s_plan", "Write", "Read", "WriteCore", "ReadCore", "ToAvroBytes",
            "TryWriteAvroBytes", "WriteAvroBytes", "WriteTo", "ReadFrom", "FromAvroBytes", "ReadResolved", "ReadField",
            "ReadPromoted", "ValueSerializer", "Get", "Put", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize",
        };
        for (var k = 0; k < Chunks(record.Fields.Count).Count; k++)
        {
            used.Add("WriteFields" + Int(k));
            used.Add("ReadFields" + Int(k));
            used.Add("ReadField" + Int(k));
        }

        for (var i = 0; i < record.Fields.Count; i++)
        {
            used.Add("s_default" + Int(i));
        }

        // Which field took each name, to explain a rename.
        var fieldNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new string[record.Fields.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var field = record.Fields[i].Name;
            var name = naming == PropertyNaming.Avro ? field : CSharpNames.Pascal(field);
            var candidate = name;
            for (var suffix = 1; !used.Add(candidate); suffix++)
            {
                candidate = name + new string('_', suffix);
            }

            if (!string.Equals(candidate, name, StringComparison.Ordinal))
            {
                var owner = fieldNames.TryGetValue(name, out var other) ? $"field '{other}'" : "a generated member or the type";
                notes.Add($"Field '{record.FullName}.{field}' is property {candidate}: {name} is taken by {owner}.");
            }

            fieldNames[candidate] = field;

            // Avro names are valid C# identifiers except for keywords, which are escaped.
            result[i] = CSharpNames.Identifier(candidate);
        }

        return result;
    }

    private static void Doc(CodeWriter w, string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            return;
        }

        w.Line("/// <summary>");
        foreach (var line in CSharpNames.CommentLines(doc!))
        {
            foreach (var wrapped in Wrap(CSharpNames.Xml(line.Trim())))
            {
                w.Line("/// " + wrapped);
            }
        }

        w.Line("/// </summary>");
    }

    // Wraps text at spaces into lines of at most DocWidth characters; a longer word keeps its own line.
    private static IEnumerable<string> Wrap(string text)
    {
        if (text.Length <= DocWidth)
        {
            yield return text;
            yield break;
        }

        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > DocWidth)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        yield return line.ToString();
    }
}
