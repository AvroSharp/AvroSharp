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

    /// <summary>
    /// Gets whether generated types also work with Apache.Avro's specific API (<c>SpecificDatumWriter</c>/<c>Reader</c>):
    /// records implement <c>Avro.Specific.ISpecificRecord</c>, fixed types derive from <c>Avro.Specific.SpecificFixed</c>,
    /// and logical types use Apache's .NET types (<c>DateTime</c> for dates and timestamps, <c>TimeSpan</c> for times,
    /// <c>Guid</c> for string UUIDs, <c>Avro.AvroDecimal</c> for decimals), whatever <see cref="LogicalTypes"/> says.
    /// The generated code then needs a reference to Apache.Avro. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ApacheCompatible { get; init; }
}
