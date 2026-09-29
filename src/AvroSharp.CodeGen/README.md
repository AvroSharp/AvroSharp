# AvroSharp.CodeGen

The C# code generation engine behind [AvroSharp](https://github.com/zcsizmadia/AvroSharp)'s source generator. It turns parsed Avro schemas into C# source: records, enums and fixed types with serializers that call `AvroWriter`/`AvroReader` directly, with no reflection.

> **Status:** an early preview (0.1). The API may still change before 1.0. Most projects should reference `AvroSharp.Generators` instead, which runs this engine inside the compiler. Use this package to generate code from your own tools.

**[Documentation](https://zcsizmadia.github.io/AvroSharp/)** · [API reference](https://zcsizmadia.github.io/AvroSharp/docs/api/index.html) · [Code generation guide](https://zcsizmadia.github.io/AvroSharp/docs/code-generation.html)

## Usage

```csharp
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

var schemas = new[] { AvroSchema.Parse(File.ReadAllText("order.avsc")) };
var options = new CodeGenOptions { DefaultNamespace = "Shop", PropertyNaming = PropertyNaming.PascalCase };

foreach (var source in CSharpCodeGenerator.Generate(schemas, options))
{
    File.WriteAllText(Path.Combine("Generated", source.HintName), source.Text);
}
```

`CodeGenOptions` also selects how logical types are mapped (`LogicalTypes`), the Apache.Avro compatibility mode (`ApacheCompatible`), property names (`PropertyNaming`), whether the target framework has `DateOnly`/`TimeOnly` (`TargetHasDateOnly`), and the C# version the code may use (`NullableAnnotations` for C# 8; `LanguageVersion`, where 11 or later adds `IAvroSerializable<T>` on .NET 8 and later).

The generated code needs the `AvroSharp` package at runtime. This package's public API will be reviewed before 1.0 ([#73](https://github.com/zcsizmadia/AvroSharp/issues/73)).
