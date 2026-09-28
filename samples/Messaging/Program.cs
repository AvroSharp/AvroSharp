// Objects on the wire: single-object messages, schema-registry framing, and streams of objects without a container.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Messages;
using AvroSharp.Schemas;
using AvroSharp.Streams;

var v1 = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Reading","namespace":"iot","fields":[{"name":"sensor","type":"string"},{"name":"value","type":"float"}]}""");
var v2 = (RecordSchema)AvroSchema.Parse("""{"type":"record","name":"Reading","namespace":"iot","fields":[{"name":"sensor","type":"string"},{"name":"value","type":"double"},{"name":"unit","type":"string","default":"C"}]}""");
var reading = new GenericRecord(v1) { ["sensor"] = "t-1", ["value"] = 21.5f };
var writer = GenericDatumWriter.Create(v1);

// Single-object encoding: C3 01, the writer schema's CRC-64 fingerprint, then the data. The reader finds the schema
// by its fingerprint and resolves every message to the schema it asks for.
byte[] message = AvroMessage.ToArray(reading, writer);
var messages = AvroMessageReader.CreateGeneric(new AvroSchemaStore(v1, v2), readerSchema: v2);
var fromMessage = messages.Read(message).AsRecord();
Console.WriteLine($"Single-object message: {message.Length} bytes -> value {fromMessage["value"].AsDouble()}, unit {fromMessage["unit"].AsString()}");

// Schema-registry framing (here Confluent's: 0x00 and a 4-byte schema ID). The resolver maps IDs to schemas; a real
// one would call the registry and cache the answers.
var registry = new Samples.InMemoryRegistry { [1] = v1, [2] = v2 };
byte[] framed = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, AvroSchemaId.FromNumber(1), reading, writer);
var framedReader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, registry);
var fromRegistry = (await framedReader.ReadAsync(framed)).AsRecord();
Console.WriteLine($"Confluent-framed message: {framed.Length} bytes, schema ID 1 -> sensor {fromRegistry["sensor"].AsString()}");

// A stream of objects with no container or framing, for a socket or a file of concatenated objects. The schema is
// not in the stream: both sides must know it.
using var stream = new MemoryStream();
await using (var streamWriter = AvroStreamWriter.CreateGeneric(stream, v1, new AvroStreamOptions { LeaveOpen = true }))
{
    for (var i = 0; i < 100; i++)
    {
        await streamWriter.WriteAsync(new GenericRecord(v1) { ["sensor"] = $"t-{i % 4}", ["value"] = i / 2f });
    }
}

stream.Position = 0;
var total = 0.0;
var count = 0;
await using (var streamReader = AvroStreamReader.OpenGeneric(stream, writerSchema: v1, readerSchema: v2, new AvroStreamOptions { LeaveOpen = true }))
{
    await foreach (var value in streamReader.ReadAllAsync())
    {
        total += value.AsRecord()["value"].AsDouble();
        count++;
    }
}

Console.WriteLine($"Stream of objects: {stream.Length} bytes, {count} readings, sum {total}");

var ok = Math.Abs(fromMessage["value"].AsDouble() - 21.5) < 1e-9 && string.Equals(fromMessage["unit"].AsString(), "C", StringComparison.Ordinal)
    && fromRegistry.Equals(reading)
    && count == 100 && Math.Abs(total - 2475) < 1e-9;
Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;

namespace Samples
{
    /// <summary>Schema IDs to schemas, in memory. A registry client would fetch unknown IDs and cache them.</summary>
    internal sealed class InMemoryRegistry : Dictionary<long, AvroSchema>, IAvroSchemaIdResolver
    {
        public AvroSchema? GetSchema(AvroSchemaId id) => TryGetValue(id.Number, out var schema) ? schema : null;

        public ValueTask<AvroSchema?> GetSchemaAsync(AvroSchemaId id, CancellationToken cancellationToken = default) => new(GetSchema(id));
    }
}
