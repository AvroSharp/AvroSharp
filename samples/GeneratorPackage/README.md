# GeneratorPackage

What an application writes to generate C# types from Avro schema files with the [`AvroSharp.Generators`](https://www.nuget.org/packages/AvroSharp.Generators) package. [GeneratorPackage.csproj](GeneratorPackage.csproj) is the whole setup, and [Program.cs](Program.cs) uses the types.

In this repository, [`samples/Directory.Build.targets`](../Directory.Build.targets) replaces the package with the generator built from source, so CI tests the current code. Copied out of the repository, the project uses the package from nuget.org.

## The project file

```xml
<ItemGroup>
  <PackageReference Include="AvroSharp.Generators" Version="..." />
  <AdditionalFiles Include="Schemas\*.avsc" />
</ItemGroup>
```

The package brings in the `AvroSharp` runtime package, so the application needs no other reference. Every `.avsc` file passed as `AdditionalFiles` is generated, and all of them are read together: [order.avsc](Schemas/order.avsc) uses `com.example.crm.Customer`, which [customer.avsc](Schemas/customer.avsc) defines.

## Requirements

The generator needs the **.NET 10 SDK** or **Visual Studio 2026** and later, since it runs AvroSharp inside the compiler. The [global.json](global.json) here asks for the .NET 10 SDK. On an older SDK, the compiler doesn't load the generator: warning CS9057 or CS8785, then errors for every generated type the code uses. The generated code itself runs on every target AvroSharp supports, down to .NET Standard 2.0 and .NET Framework.

## Settings

The sample sets each of them:

| Property | Here | Effect |
|---|---|---|
| `AvroSharpNamespace` | `Shop` | The C# namespace of types without an Avro namespace: [heartbeat.avsc](Schemas/heartbeat.avsc) becomes `Shop.Heartbeat`. |
| `AvroSharpNamespaceMap` | `com.example.shop:Shop.Orders;com.example.crm:Shop.Customers` | C# namespaces for Avro namespaces, and those under them. The longest match wins. |
| `AvroSharpPropertyNames` | `pascal` | `order_id` becomes `OrderId` (the default). `avro` keeps the Avro names, as Apache's avrogen does. |
| `AvroSharpLogicalTypes` | `native` | `uuid` is a `Guid`, `timestamp-millis` a `DateTimeOffset` and `decimal` a `decimal` (the default). `raw` keeps the underlying `string`, `long` and `byte[]`. |
| `AvroSharpApacheCompatible` | not set | `true` makes the types Apache.Avro's `ISpecificRecord` too. The [Migration](../Migration/Program.cs) sample shows it. |

The namespaces are C# only: the schemas keep their Avro names, so the data and the fingerprints stay the same. A value the generator doesn't recognize is warning AVROGEN006, and the default is used. The [code generation guide](../../docs/code-generation.md) has every option and the type mapping.

## Seeing the generated code

With `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`, the build also writes the generated files, one per named type, under the project's `obj/` folder (`generated/AvroSharp.Generators/`). Visual Studio and Rider also show them under the project's analyzers.

The generator reports as compiler diagnostics, AVROGEN001 to AVROGEN006: a schema file that isn't valid (with its file, line and column), a missing runtime reference, a schema the generator can't generate code for, `AvroSharpApacheCompatible` without a reference to Apache.Avro, an unrecognized setting, and, as information, a member it renamed to avoid a clash. The code generation guide lists them.

## Building the generator from source

For work on the generator, or a monorepo that builds it, a project can reference the generator's project instead of the package. That is not the supported way for applications: the package is. It's what [`build/UseLocalGenerator.targets`](../../build/UseLocalGenerator.targets) does, and it needs what the package otherwise brings:

- **The analyzer reference:** `<ProjectReference Include="...\AvroSharp.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`.
- **The generator's dependencies:** `AvroSharp.dll` (its netstandard2.0 build) and `AvroSharp.CodeGen.dll`, as `Analyzer` items from the generator's output folder. The compiler loads only the assemblies passed to it as analyzers and doesn't follow the generator's references; the package ships them next to the generator in `analyzers/dotnet/cs`.
- **The settings:** importing the package's `build/AvroSharp.Generators.props`, which makes the settings visible to the compiler, and `build/AvroSharp.Generators.targets`. A project reference imports neither.
- **The runtime:** a normal reference to `AvroSharp`, since the analyzer reference gives no runtime assembly.

Two caveats:

- Visual Studio keeps a loaded generator, so after changing the generator it may need a restart to use the new build.
- Don't pass a shared `OutDir` or `OutputPath` on to the generator's reference, or its netstandard2.0 `AvroSharp.dll` can overwrite the application's own. BenchmarkDotNet builds that way; the targets file removes both.
