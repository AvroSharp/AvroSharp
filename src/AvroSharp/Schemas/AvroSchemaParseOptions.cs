namespace AvroSharp.Schemas;

/// <summary>Options for <see cref="AvroSchemaParser"/>.</summary>
public sealed class AvroSchemaParseOptions
{
    /// <summary>Gets the default options.</summary>
    public static AvroSchemaParseOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether names, namespaces, field names and enum symbols must follow the
    /// specification's <c>[A-Za-z_][A-Za-z0-9_]*</c> rule. Defaults to <see langword="true"/>.
    /// Disable only to read legacy schemas produced by non-conforming tools.
    /// </summary>
    public bool ValidateNames { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether field default values must match the field schema, as described in the
    /// specification. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateDefaults { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether JavaScript-style comments and trailing commas are accepted.
    /// Defaults to <see langword="false"/> (strict JSON).
    /// </summary>
    public bool AllowComments { get; init; }

    /// <summary>Gets the maximum JSON nesting depth. Defaults to 256.</summary>
    public int MaxDepth { get; init; } = 256;
}
