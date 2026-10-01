using System.Collections.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.CodeGen;

/// <summary>
/// A record whose C# type the user declared (the attribute-driven generator's <c>[AvroSerializable]</c>): the
/// generator adds its schema and serializers to the <see langword="partial"/> type, and declares no properties.
/// </summary>
/// <param name="Schema">The record's schema, built from the type.</param>
/// <param name="Namespace">The C# namespace, or <see langword="null"/> for the global namespace.</param>
/// <param name="Name">The C# type's name.</param>
/// <param name="IsRecordClass">Whether the type is a <c>record class</c>, which its partial declaration must say.</param>
/// <param name="Members">The C# member of each field, in field order, as identifiers.</param>
/// <param name="DeclaresConstructors">
/// Whether the type declares constructors. When it doesn't, the generator declares the public parameterless one the
/// compiler would have added, which the readers' own constructor would otherwise take away.
/// </param>
internal sealed record DeclaredRecord(RecordSchema Schema, string? Namespace, string Name, bool IsRecordClass, IReadOnlyList<string> Members, bool DeclaresConstructors);

/// <summary>A C# enum the user declared, which a <see cref="DeclaredRecord"/> uses for an Avro enum.</summary>
/// <param name="AvroFullName">The Avro enum's full name.</param>
/// <param name="Namespace">The C# namespace, or <see langword="null"/>.</param>
/// <param name="Name">The C# enum's name.</param>
internal sealed record DeclaredEnum(string AvroFullName, string? Namespace, string Name);
