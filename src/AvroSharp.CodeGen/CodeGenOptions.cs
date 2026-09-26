namespace AvroSharp.CodeGen;

/// <summary>Options for <see cref="CSharpCodeGenerator"/>.</summary>
public sealed class CodeGenOptions
{
    /// <summary>Gets the default options.</summary>
    public static CodeGenOptions Default { get; } = new();

    /// <summary>
    /// Gets the C# namespace for named types that have no Avro namespace. Defaults to <see langword="null"/>: such
    /// types are generated in the global namespace.
    /// </summary>
    public string? DefaultNamespace { get; init; }

    /// <summary>Gets how values of logical types are represented. Defaults to <see cref="LogicalTypeMapping.Native"/>.</summary>
    public LogicalTypeMapping LogicalTypes { get; init; } = LogicalTypeMapping.Native;

    /// <summary>
    /// Gets whether the target framework has <c>System.DateOnly</c> and <c>System.TimeOnly</c> (.NET 6 and later).
    /// When it does not, <c>date</c> maps to <c>DateTime</c> and <c>time-*</c> to <c>TimeSpan</c>. Defaults to <see langword="true"/>.
    /// </summary>
    public bool TargetHasDateOnly { get; init; } = true;
}
