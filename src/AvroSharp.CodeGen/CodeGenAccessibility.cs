namespace AvroSharp.CodeGen;

/// <summary>The accessibility of generated types (<see cref="CodeGenOptions.Accessibility"/>).</summary>
public enum CodeGenAccessibility
{
    /// <summary><c>public</c> types.</summary>
    Public,

    /// <summary>
    /// <c>internal</c> types, for a library that keeps its Avro types to itself. A public type may not use an internal
    /// one: generation fails, naming both, rather than the compiler (CS0053).
    /// </summary>
    Internal,
}
