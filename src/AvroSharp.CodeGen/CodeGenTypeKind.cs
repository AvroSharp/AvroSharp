namespace AvroSharp.CodeGen;

/// <summary>What kind of C# type an Avro record becomes (<see cref="CodeGenOptions.TypeKind"/>).</summary>
public enum CodeGenTypeKind
{
    /// <summary>A <c>sealed partial class</c>, with reference equality.</summary>
    Class,

    /// <summary>
    /// A <c>sealed partial record class</c> (C# 9 or later): value equality and <c>with</c> expressions, member by member,
    /// so lists, dictionaries and byte arrays compare by reference, as in any C# record.
    /// </summary>
    Record,
}
