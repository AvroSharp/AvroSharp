---
uid: AvroSharp.CodeGen
summary: *content
---
The C# code generation engine of the `AvroSharp.CodeGen` package, which the source generator and the `avrosharp` tool use. [`CSharpCodeGenerator.Generate`](xref:AvroSharp.CodeGen.CSharpCodeGenerator.Generate%2A) turns schemas from <xref:AvroSharp.Schemas> into [`GeneratedSource`](xref:AvroSharp.CodeGen.GeneratedSource) files. [`CodeGenOptions`](xref:AvroSharp.CodeGen.CodeGenOptions) sets the namespace, the property names, the C# language version and the .NET types of logical types ([`LogicalTypeMapping`](xref:AvroSharp.CodeGen.LogicalTypeMapping)). [`SchemaFileSet`](xref:AvroSharp.CodeGen.SchemaFileSet) parses a set of schema files together, so a file can refer to named types that another file defines. The generated types implement the interfaces of <xref:AvroSharp.Serialization>.
