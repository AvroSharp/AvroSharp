using System;

namespace AvroSharp.Serialization;

/// <summary>How the attribute-driven generator turns C# member names into Avro field names.</summary>
/// <seealso cref="AvroSerializableAttribute.FieldNames"/>
/// <seealso cref="AvroNamingPolicyAttribute"/>
public enum AvroNaming
{
    /// <summary>The member's name as written, as Apache Avro's Java reflection and Apache.Avro's <c>[AvroField]</c> matching use it.</summary>
    AsWritten,

    /// <summary>
    /// The first letter in lower case (<c>OrderId</c> is <c>orderId</c>), the convention of schemas written for Java
    /// and most other languages. It is the inverse of the <c>.avsc</c> generator's default PascalCase property names.
    /// </summary>
    CamelCase,
}

/// <summary>
/// Generates an Avro schema and serializers for a <see langword="partial"/> class or record class, with the members
/// that types generated from <c>.avsc</c> files have: <c>Schema</c>, <c>Write</c>, <c>Read</c>, <c>ToAvroBytes</c>,
/// <c>FromAvroBytes</c> and <see cref="IAvroWritable"/>, <see cref="IAvroReadable"/> and, on .NET 8 and later,
/// <c>IAvroSerializable&lt;T&gt;</c>. The <c>AvroSharp.Generators</c> package provides the generator.
/// </summary>
/// <remarks>
/// The fields are the public instance properties with a getter and a setter, and the public instance fields, in
/// declaration order (inherited ones first), except those marked <see cref="AvroIgnoreAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AvroSerializableAttribute : Attribute
{
    /// <summary>Gets or sets the record's Avro name. The default is the C# type's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the record's Avro namespace. The default is the C# type's namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets the record's <c>doc</c>. The default is the type's XML <c>summary</c>, when the compilation has documentation comments.</summary>
    public string? Doc { get; set; }

    /// <summary>
    /// Gets or sets how member names become field names. The default is the assembly's
    /// <see cref="AvroNamingPolicyAttribute"/>, or <see cref="AvroNaming.AsWritten"/>.
    /// </summary>
    public AvroNaming FieldNames { get; set; }
}

/// <summary>The default <see cref="AvroSerializableAttribute.FieldNames"/> for every type in the assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AvroNamingPolicyAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroNamingPolicyAttribute"/> class.</summary>
    /// <param name="fieldNames">How member names become field names.</param>
    public AvroNamingPolicyAttribute(AvroNaming fieldNames) => FieldNames = fieldNames;

    /// <summary>Gets how member names become field names.</summary>
    public AvroNaming FieldNames { get; }
}

/// <summary>The Avro name of a field, an enum symbol, or an enum, used as is, without the naming policy.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum)]
public sealed class AvroNameAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroNameAttribute"/> class.</summary>
    /// <param name="name">The Avro name.</param>
    public AvroNameAttribute(string name) => Name = name;

    /// <summary>Gets the Avro name.</summary>
    public string Name { get; }
}

/// <summary>An alias of a record, an enum or a field: the name an earlier version of the schema gave it.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
public sealed class AvroAliasAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroAliasAttribute"/> class.</summary>
    /// <param name="alias">The alias.</param>
    public AvroAliasAttribute(string alias) => Alias = alias;

    /// <summary>Gets the alias.</summary>
    public string Alias { get; }
}

/// <summary>The <c>doc</c> of a field or an enum, instead of its XML <c>summary</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum)]
public sealed class AvroDocAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroDocAttribute"/> class.</summary>
    /// <param name="doc">The documentation.</param>
    public AvroDocAttribute(string doc) => Doc = doc;

    /// <summary>Gets the documentation.</summary>
    public string Doc { get; }
}

/// <summary>Leaves a property or field out of the schema.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroIgnoreAttribute : Attribute
{
}

/// <summary>
/// The field's default, as Avro JSON (<c>"0"</c>, <c>"\"none\""</c>, <c>"[]"</c>): the value a reader gives the
/// field when the data was written without it. A nullable member has <c>null</c> without this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroDefaultAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroDefaultAttribute"/> class.</summary>
    /// <param name="json">The default, as Avro JSON.</param>
    public AvroDefaultAttribute(string json) => Json = json;

    /// <summary>Gets the default, as Avro JSON.</summary>
    public string Json { get; }
}

/// <summary>The precision and scale of a <see langword="decimal"/> member, which Avro's <c>decimal</c> needs.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroDecimalAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroDecimalAttribute"/> class.</summary>
    /// <param name="precision">The number of digits, 1 to 28.</param>
    /// <param name="scale">The digits after the decimal point, 0 to <paramref name="precision"/>.</param>
    public AvroDecimalAttribute(int precision, int scale)
    {
        Precision = precision;
        Scale = scale;
    }

    /// <summary>Gets the number of digits.</summary>
    public int Precision { get; }

    /// <summary>Gets the digits after the decimal point.</summary>
    public int Scale { get; }
}

/// <summary>Makes a <c>byte[]</c> or <see cref="Guid"/> member a <c>fixed</c> type of <see cref="Size"/> bytes, instead of <c>bytes</c> or <c>string</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroFixedAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroFixedAttribute"/> class.</summary>
    /// <param name="size">The size in bytes (16 for a <see cref="Guid"/>).</param>
    public AvroFixedAttribute(int size) => Size = size;

    /// <summary>Gets the size in bytes.</summary>
    public int Size { get; }

    /// <summary>Gets or sets the fixed type's name. The default is the member's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the fixed type's namespace. The default is the record's.</summary>
    public string? Namespace { get; set; }
}

/// <summary>
/// The logical type of a member, instead of its type's default (<c>timestamp-millis</c> for a
/// <see cref="DateTimeOffset"/>, which is <c>timestamp-micros</c> by default). On a <see langword="long"/>,
/// <see langword="int"/> or <see langword="string"/> member it annotates the raw value.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroLogicalTypeAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroLogicalTypeAttribute"/> class.</summary>
    /// <param name="name">The logical type's name, such as <c>timestamp-millis</c>.</param>
    public AvroLogicalTypeAttribute(string name) => Name = name;

    /// <summary>Gets the logical type's name.</summary>
    public string Name { get; }
}

/// <summary>
/// Makes an <see cref="object"/> member a union of the given <see cref="AvroSerializableAttribute">serializable</see>
/// types (and <c>null</c> when the member is nullable). A value is written as the branch of its runtime type.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroUnionAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="AvroUnionAttribute"/> class.</summary>
    /// <param name="types">The branches' types, in order.</param>
    public AvroUnionAttribute(params Type[] types) => Types = types;

    /// <summary>Gets the branches' types.</summary>
    public Type[] Types { get; }
}

/// <summary>Marks the enum member that is the enum's <c>default</c>: the symbol a reader gives a symbol it doesn't know.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class AvroEnumDefaultAttribute : Attribute
{
}

/// <summary>
/// The field's position among the record's fields. Needed only when the fields are declared in more than one part of
/// a partial type, whose order the compiler doesn't fix; then every field needs it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AvroFieldAttribute : Attribute
{
    /// <summary>Gets or sets the position, from 0.</summary>
    public int Order { get; set; } = -1;
}
