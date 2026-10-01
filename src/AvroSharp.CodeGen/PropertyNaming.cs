namespace AvroSharp.CodeGen;

/// <summary>How record fields become C# property names.</summary>
/// <seealso cref="CodeGenOptions.PropertyNames"/>
public enum PropertyNaming
{
    /// <summary>PascalCase: <c>customer_name</c> becomes <c>CustomerName</c>.</summary>
    PascalCase,

    /// <summary>
    /// The Avro field name as written (<c>customer_name</c>), as Apache.Avro's <c>avrogen</c> generates it; C#
    /// keywords are escaped (<c>@class</c>). Existing code written against avrogen classes then compiles unchanged.
    /// </summary>
    Avro,
}
