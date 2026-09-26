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
}
