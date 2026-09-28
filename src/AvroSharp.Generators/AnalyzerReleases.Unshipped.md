; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AVROGEN001 | AvroSharp | Error | An .avsc file is not a valid Avro schema
AVROGEN002 | AvroSharp | Error | The project does not reference the AvroSharp runtime
AVROGEN003 | AvroSharp | Error | Code generation failed
AVROGEN004 | AvroSharp | Error | AvroSharpApacheCompatible is set without a reference to Apache.Avro
AVROGEN005 | AvroSharp | Info | A field's property was renamed to avoid a clash with another member
