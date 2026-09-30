using System;
using System.Globalization;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>
/// The Apache.Avro compatibility mode (<see cref="CodeGenOptions.ApacheCompatible"/>): what generated types add so
/// that Apache's <c>SpecificDatumWriter</c>/<c>SpecificDatumReader</c> can use them. Apache.Avro 1.12.2 finds a type
/// by the schema's full name and creates it with a parameterless constructor; it reads and writes record fields
/// through <c>ISpecificRecord.Get</c>/<c>Put</c> and fixed values through <c>SpecificFixed.Value</c>.
/// </summary>
internal static class ApacheSupport
{
    public const string SpecificRecord = "global::Avro.Specific.ISpecificRecord";
    public const string Namespace = "AvroSharp.Generated";
    public const string HintName = Namespace + ".ApacheDecimals.g.cs";

    private const string ApacheSchema = "global::Avro.Schema";

    /// <summary>
    /// The schema JSON Apache.Avro can parse. It rejects <c>uuid</c> on <c>fixed(16)</c>, which the specification
    /// allows; without the logical type the fixed is the same 16 bytes, and the compatibility mode keeps it a fixed type.
    /// </summary>
    public static string SchemaJson(string json) =>
        json.ReplaceOrdinal("\"size\":16,\"logicalType\":\"uuid\"", "\"size\":16");

    /// <summary>
    /// The schema as avrogen's classes expose it: a static <c>_SCHEMA</c> and an instance <c>Schema</c> (which also
    /// implements <c>ISpecificRecord.Schema</c>), so code written for avrogen output, such as
    /// <c>new SpecificDatumWriter&lt;T&gt;(value.Schema)</c>, compiles unchanged.
    /// </summary>
    public static void EmitRecordSchema(CodeWriter w, TypeMapper types)
    {
        w.Line();
        w.Line($"private static {types.Nullable(ApacheSchema)} s_apacheSchema;");
        w.Line();
        w.Line("/// <summary>Gets the schema as Apache.Avro represents it, as avrogen's generated classes have it.</summary>");
        w.Line($"public static {ApacheSchema} _SCHEMA => s_apacheSchema ?? (s_apacheSchema = {ApacheSchema}.Parse(ApacheSchemaJson));");
        w.Line();
        w.Line("/// <summary>Gets the schema as Apache.Avro represents it (<c>ISpecificRecord.Schema</c>); AvroSharp's is <c>AvroSharpSchema</c>.</summary>");
        w.Line($"public {ApacheSchema} Schema => _SCHEMA;");
    }

    /// <summary>Explicit <c>ISpecificRecord</c> members, delegating to the generated <c>Get</c>/<c>Put</c>.</summary>
    public static void EmitRecordMembers(CodeWriter w, TypeMapper types)
    {
        w.Line();
        w.Line($"object {SpecificRecord}.Get(int fieldPos) => Get(fieldPos){types.NullForgiving};");
        w.Line();
        w.Line($"void {SpecificRecord}.Put(int fieldPos, object fieldValue) => Put(fieldPos, fieldValue);");
    }

    /// <summary>
    /// A fixed type deriving from <c>SpecificFixed</c>, which holds the bytes in its <c>Value</c> and supplies value
    /// equality. Its abstract instance <c>Schema</c> takes the name, so AvroSharp's schema is <c>AvroSharpSchema</c> here.
    /// </summary>
    public static void EmitFixed(CodeWriter w, FixedSchema schema, string name, TypeMapper types, Action<FixedSchema> emitSchemaMembers)
    {
        w.Open($"public sealed partial class {name} : global::Avro.Specific.SpecificFixed");
        w.Line("/// <summary>The number of bytes in a value.</summary>");
        w.Line($"public const int Size = {schema.Size.ToString(CultureInfo.InvariantCulture)};");
        w.Line();
        emitSchemaMembers(schema);
        w.Line();
        w.Line($"private static {types.Nullable(ApacheSchema)} s_apacheSchema;");
        w.Line();
        w.Line("/// <summary>Creates a value of zeros; Apache.Avro's reader then sets <c>Value</c>.</summary>");
        w.Line($"public {name}()");
        w.Line("    : base(Size)");
        w.Open();
        w.Close();
        w.Line();
        w.Line("/// <summary>Creates a value from exactly <see cref=\"Size\"/> bytes. The array is not copied.</summary>");
        w.Line($"public {name}(byte[] value)");
        w.Line("    : base(Size)");
        w.Open();
        w.Line($"Value = global::AvroSharp.Serialization.Generated.AvroGeneratedCode.CheckFixedSize(value, Size, {CSharpNames.Literal(schema.FullName)});");
        w.Close();
        w.Line();
        w.Line("/// <summary>Gets the schema as Apache.Avro represents it, as avrogen's generated classes have it.</summary>");
        w.Line($"public static {ApacheSchema} _SCHEMA => s_apacheSchema ?? (s_apacheSchema = {ApacheSchema}.Parse(ApacheSchemaJson));");
        w.Line();
        w.Line("/// <summary>Gets the schema, as Apache.Avro represents it.</summary>");
        w.Line($"public override {ApacheSchema} Schema => _SCHEMA;");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line($"public override bool Equals({types.Nullable("object")} obj) => base.Equals(obj);");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line("public override int GetHashCode() => base.GetHashCode();");
        CSharpCodeGenerator.EmitFixedOperators(w, name, types);
        w.Close();
    }

    /// <summary>
    /// The conversions between Apache's <c>AvroDecimal</c> and the decimal encoding, generated into the consumer
    /// (AvroSharp does not reference Apache.Avro). They match Apache's own: the scale must equal the schema's, and
    /// the unscaled value is minimal two's-complement big-endian, sign-extended on <c>fixed</c>.
    /// </summary>
    public static string Source(string version, bool annotations) =>
        Template.ReplaceOrdinal("{{VERSION}}", CSharpNames.Literal(version)).ReplaceOrdinal("{{NULLABLE}}", annotations ? "#nullable enable" : string.Empty);

    private const string Template = """
        // <auto-generated/>
        // Generated by AvroSharp.CodeGen for the Apache.Avro compatibility mode. Do not edit.
        {{NULLABLE}}

        namespace AvroSharp.Generated
        {
            /// <summary>Converts Apache.Avro's AvroDecimal to and from the Avro decimal encoding.</summary>
            [global::System.CodeDom.Compiler.GeneratedCode("AvroSharp.CodeGen", {{VERSION}})]
            internal static class ApacheDecimals
            {
                public static void WriteBytes(ref global::AvroSharp.IO.AvroWriter writer, global::Avro.AvroDecimal value, int scale) =>
                    writer.WriteBytes(Unscaled(value, scale, -1));

                public static void WriteFixed(ref global::AvroSharp.IO.AvroWriter writer, global::Avro.AvroDecimal value, int scale, int size) =>
                    writer.WriteFixed(Unscaled(value, scale, size));

                /// <summary>The fixed bytes of a decimal that Apache's reader puts as an AvroDecimal.</summary>
                public static byte[] FixedBytes(global::Avro.AvroDecimal value, int scale, int size) =>
                    Unscaled(value, scale, size);

                public static global::Avro.AvroDecimal ReadBytes(ref global::AvroSharp.IO.AvroReader reader, int scale) =>
                    FromUnscaled(reader.ReadBytesSpan().ToArray(), scale);

                public static global::Avro.AvroDecimal ReadFixed(ref global::AvroSharp.IO.AvroReader reader, int scale, int size) =>
                    FromUnscaled(reader.ReadFixedSpan(size).ToArray(), scale);

                private static byte[] Unscaled(global::Avro.AvroDecimal value, int scale, int size)
                {
                    if (value.Scale != scale)
                    {
                        throw new global::AvroSharp.AvroException("The decimal's scale " + value.Scale + " is not the schema's scale " + scale + ".");
                    }

                    // BigInteger gives the fewest two's-complement bytes, little-endian.
                    var bytes = value.UnscaledValue.ToByteArray();
                    global::System.Array.Reverse(bytes);
                    if (size < 0 || bytes.Length == size)
                    {
                        return bytes;
                    }

                    if (bytes.Length > size)
                    {
                        throw new global::AvroSharp.AvroException("The decimal does not fit in " + size + " bytes.");
                    }

                    var padded = new byte[size];
                    if (value.UnscaledValue.Sign < 0)
                    {
                        for (var i = 0; i < size - bytes.Length; i++)
                        {
                            padded[i] = 0xFF;
                        }
                    }

                    global::System.Array.Copy(bytes, 0, padded, size - bytes.Length, bytes.Length);
                    return padded;
                }

                private static global::Avro.AvroDecimal FromUnscaled(byte[] bigEndian, int scale)
                {
                    if (bigEndian.Length == 0)
                    {
                        throw new global::AvroSharp.AvroDataException("A decimal has no bytes; its unscaled value needs at least one.");
                    }

                    global::System.Array.Reverse(bigEndian);
                    return new global::Avro.AvroDecimal(new global::System.Numerics.BigInteger(bigEndian), scale);
                }
            }
        }

        """;
}
