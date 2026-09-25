namespace AvroSharp.Generic;

/// <summary>The kind of value held by an <see cref="AvroValue"/>.</summary>
public enum AvroValueKind
{
    /// <summary>The Avro <c>null</c> value.</summary>
    Null,

    /// <summary>A <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>A 32-bit <see cref="int"/> (Avro <c>int</c>).</summary>
    Int,

    /// <summary>A 64-bit <see cref="long"/> (Avro <c>long</c>).</summary>
    Long,

    /// <summary>A <see cref="float"/>.</summary>
    Float,

    /// <summary>A <see cref="double"/>.</summary>
    Double,

    /// <summary>A byte array (Avro <c>bytes</c>).</summary>
    Bytes,

    /// <summary>A <see cref="string"/>.</summary>
    String,

    /// <summary>A <see cref="GenericRecord"/>.</summary>
    Record,

    /// <summary>An enum symbol: its schema and ordinal.</summary>
    Enum,

    /// <summary>A list of values (Avro <c>array</c>).</summary>
    Array,

    /// <summary>A dictionary from string keys to values (Avro <c>map</c>).</summary>
    Map,

    /// <summary>A <see cref="GenericFixed"/>.</summary>
    Fixed,
}
