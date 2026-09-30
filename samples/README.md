# Samples

Small console programs that show AvroSharp's main APIs end to end. Run one with `dotnet run --project samples/<name>`. CI runs each on every build, and each checks its own results, so they stay in step with the code.

| Sample | Shows |
|---|---|
| [GettingStarted](GettingStarted/Program.cs) | Parsing a schema, writing and reading a `GenericRecord`, reading data as a newer schema version, JSON, fingerprints |
| [GeneratedTypes](GeneratedTypes/Program.cs) | C# types generated from [a schema file](GeneratedTypes/Schemas/order.avsc) with logical types, a zstandard container file, reading every codec, and reading a file an older schema version wrote |
| [Messaging](Messaging/Program.cs) | Single-object messages with a schema store, Confluent schema-registry framing with an ID resolver, and a stream of objects without a container |
| [Migration](Migration/Program.cs) | Apache.Avro code and the same with AvroSharp, each reading what the other wrote: schemas and fingerprints, the generic model, resolution, container files, generated types in the Apache.Avro compatibility mode, and exceptions. It holds the code of [the migration guide](../docs/migrating-from-apache-avro.md) |

The GeneratedTypes project uses the source generator from this repository (`build/UseLocalGenerator.targets`). An application references the `AvroSharp.Generators` package instead, as its project file notes.
