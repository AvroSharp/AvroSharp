; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AVROGEN006 | AvroSharp | Warning | An AvroSharp MSBuild property has a value the generator does not recognize
AVROGEN101 | AvroSharp | Error | An [AvroSerializable] type is not a partial, non-abstract class or record class
AVROGEN102 | AvroSharp | Error | A member's type has no Avro mapping, or is not the type its field is read as
AVROGEN103 | AvroSharp | Error | A decimal member has no [AvroDecimal] precision and scale
AVROGEN104 | AvroSharp | Error | A name is not a valid Avro name
AVROGEN105 | AvroSharp | Error | Two members have the same Avro field name
AVROGEN106 | AvroSharp | Error | An [AvroDefault] is not valid, or the built schema is not
AVROGEN107 | AvroSharp | Error | An [AvroSerializable] type is generic or nested in another type
AVROGEN108 | AvroSharp | Error | An [AvroSerializable] type has a primary constructor
AVROGEN109 | AvroSharp | Error | Field order is ambiguous across partial declarations without [AvroFieldPosition]
AVROGEN110 | AvroSharp | Error | [AvroUnion] is not on an object member, or lists a type that is not a class
AVROGEN111 | AvroSharp | Error | A member uses a class that is not [AvroSerializable], or whose attribute has errors
AVROGEN112 | AvroSharp | Error | An Avro attribute does not apply to the member it is on
AVROGEN113 | AvroSharp | Error | Two C# types define the same Avro name
AVROGEN114 | AvroSharp | Error | A DateTime member has no logical type
AVROGEN115 | AvroSharp | Error | A member is init-only
AVROGEN116 | AvroSharp | Error | An enum used by an [AvroSerializable] type has values other than 0, 1, 2 and so on
AVROGEN117 | AvroSharp | Error | A member has the name of a member the generator adds
AVROGEN118 | AvroSharp | Error | Code generation failed for an [AvroSerializable] type
