namespace AvroSharp.CodeGen;

/// <summary>The C# type of Avro arrays in generated records (<see cref="CodeGenOptions.CollectionType"/>).</summary>
public enum CodeGenCollectionType
{
    /// <summary><c>List&lt;T&gt;</c>: readers reuse a record's lists (<c>ReadFrom</c>), and writers write from their memory.</summary>
    List,

    /// <summary><c>T[]</c>: read into a list and copied to an array once complete.</summary>
    Array,

    /// <summary>
    /// <c>IReadOnlyList&lt;T&gt;</c>: readers give a <c>List&lt;T&gt;</c>; any list or array can be set. Writers take a
    /// <c>List&lt;T&gt;</c> or an array from its memory, and any other list item by item.
    /// </summary>
    ReadOnlyList,
}
