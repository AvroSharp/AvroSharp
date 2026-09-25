namespace AvroSharp.Schemas;

/// <summary>The Avro schema types.</summary>
public enum AvroSchemaType
{
    /// <summary>The <c>null</c> primitive type.</summary>
    Null,

    /// <summary>The <c>boolean</c> primitive type.</summary>
    Boolean,

    /// <summary>The <c>int</c> primitive type (32-bit signed integer).</summary>
    Int,

    /// <summary>The <c>long</c> primitive type (64-bit signed integer).</summary>
    Long,

    /// <summary>The <c>float</c> primitive type (IEEE 754 single precision).</summary>
    Float,

    /// <summary>The <c>double</c> primitive type (IEEE 754 double precision).</summary>
    Double,

    /// <summary>The <c>bytes</c> primitive type.</summary>
    Bytes,

    /// <summary>The <c>string</c> primitive type (UTF-8 text).</summary>
    String,

    /// <summary>A named record of fields.</summary>
    Record,

    /// <summary>A named enumeration of symbols.</summary>
    Enum,

    /// <summary>An array of items.</summary>
    Array,

    /// <summary>A map from string keys to values.</summary>
    Map,

    /// <summary>A union of schemas.</summary>
    Union,

    /// <summary>A named fixed-size byte sequence.</summary>
    Fixed,
}
