// Generates C# for a schema with defaults (which go through System.Text.Json) and a logical type.
using AvroSharp.CodeGen;
using AvroSharp.Schemas;

var schema = AvroSchema.Parse("""
    {"type":"record","name":"Reading","namespace":"consumer","fields":[
      {"name":"id","type":"long","default":0},
      {"name":"day","type":{"type":"int","logicalType":"date"},"default":1}]}
    """);
var sources = CSharpCodeGenerator.Generate([schema]);
var ok = sources.Count == 1 && sources[0].Text.Contains("class Reading", StringComparison.Ordinal);

Console.WriteLine(ok ? $"CodeGenApp: ok on .NET {Environment.Version}" : "CodeGenApp failed: no Reading class generated");
return ok ? 0 : 1;
