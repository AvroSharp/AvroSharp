namespace AvroSharp.Generic;

/// <summary>How <see cref="GenericDatumJsonWriter"/> labels a union value's branch.</summary>
/// <seealso cref="GenericDatumWriterOptions.JsonUnionBranchNames"/>
public enum JsonUnionBranchNames
{
    /// <summary>
    /// A named type by its full name, <c>{"com.acme.Card": {...}}</c>, as the specification says. Every Avro JSON
    /// reader reads it.
    /// </summary>
    Full,

    /// <summary>
    /// A named type by its name without the namespace, <c>{"Card": {...}}</c>, as Apache Avro Java's
    /// <c>JsonEncoder.setIncludeNamespace(false)</c> writes it. A branch keeps its full name when its simple name would
    /// be ambiguous: when another branch has the same simple name, or another branch's label is that name.
    /// <see cref="GenericDatumJsonReader"/> reads this form; Java's <c>JsonDecoder</c> and Apache.Avro C# do not.
    /// </summary>
    Simple,
}
