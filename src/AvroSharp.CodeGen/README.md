# AvroSharp.CodeGen

The C# code generation engine behind [AvroSharp](https://github.com/AvroSharp/AvroSharp)'s source generator, in the [`AvroSharp.CodeGen`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html) namespace. It turns parsed Avro schemas into C# source: records, enums and fixed types with serializers that call [`AvroWriter`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroWriter.html)/[`AvroReader`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.IO.AvroReader.html) directly, with no reflection.

> **Status:** stable. The public API follows [semantic versioning](https://semver.org/): no breaking changes before 2.0. Most projects should reference `AvroSharp.Generators` instead, which runs this engine inside the compiler. Use this package to generate code from your own tools.

**[Documentation](https://avrosharp.github.io/AvroSharp/)** · [API reference](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.html) · [Code generation guide](https://avrosharp.github.io/AvroSharp/docs/code-generation.html)

## Usage

[`CSharpCodeGenerator.Generate`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CSharpCodeGenerator.Generate.html) takes the schemas and a [`CodeGenOptions`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.html), and returns one [`GeneratedSource`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.GeneratedSource.html) per named type, and one more in the Apache.Avro compatibility mode:

```csharp
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

var schemas = new[] { AvroSchema.Parse(File.ReadAllText("order.avsc")) };
var options = new CodeGenOptions { Namespace = "Shop", PropertyNames = PropertyNaming.PascalCase };

foreach (var source in CSharpCodeGenerator.Generate(schemas, options))
{
    File.WriteAllText(Path.Combine("Generated", source.HintName), source.Text);
}
```

[`SchemaFileSet.Parse`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.SchemaFileSet.Parse.html) parses several schema files together, as the source generator and the `avrosharp` tool do, so a file may refer to named types that another file defines.

`CodeGenOptions` also selects namespace mappings ([`NamespaceMap`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.NamespaceMap.html)), how logical types are mapped ([`LogicalTypes`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.LogicalTypes.html)), the Apache.Avro compatibility mode ([`ApacheCompatible`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.ApacheCompatible.html)), property names ([`PropertyNames`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.PropertyNames.html)), whether the target framework has `DateOnly`/`TimeOnly` ([`TargetHasDateOnly`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.TargetHasDateOnly.html)), and the C# version the code may use ([`NullableAnnotations`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.NullableAnnotations.html), which needs C# 8; [`LanguageVersion`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.CodeGen.CodeGenOptions.LanguageVersion.html), 7 or later, where 7 means C# 7.2 or later and 11 or later adds [`IAvroSerializable<T>`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Serialization.IAvroSerializable-1.html) on .NET 8 and later).

The generated code needs the `AvroSharp` package at runtime.
