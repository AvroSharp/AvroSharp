namespace AvroSharp.CodeGen;

/// <summary>How generated code represents values of logical types.</summary>
/// <seealso cref="CodeGenOptions.LogicalTypes"/>
public enum LogicalTypeMapping
{
    /// <summary>
    /// .NET types: <c>date</c> as <c>DateOnly</c>, <c>time-*</c> as <c>TimeOnly</c> (<c>DateTime</c>/<c>TimeSpan</c> on
    /// frameworks without them), <c>timestamp-millis/micros</c> as <c>DateTimeOffset</c>,
    /// <c>local-timestamp-millis/micros</c> as <c>DateTime</c>, <c>uuid</c> as <c>Guid</c>, and <c>decimal</c> with a
    /// precision up to 28 as <c>decimal</c>. Other logical types keep their underlying type.
    /// </summary>
    Native,

    /// <summary>Every logical type keeps its underlying type (a <c>date</c> is an <c>int</c>).</summary>
    Raw,
}
