namespace AvroSharp.Schemas;

/// <summary>How a record field affects the sort order of records.</summary>
public enum FieldOrder
{
    /// <summary>The field sorts in ascending order (the default).</summary>
    Ascending,

    /// <summary>The field sorts in descending order.</summary>
    Descending,

    /// <summary>The field is ignored when sorting.</summary>
    Ignore,
}
