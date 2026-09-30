using System;
using System.Collections.Generic;
using AvroSharp.Generic;
using AvroSharp.Messages;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using AvroSchema = AvroSharp.Schemas.AvroSchema;
using RecordSchema = AvroSharp.Schemas.RecordSchema;

namespace AvroSharp.Benchmarks;

/// <summary>
/// Reading one framed message: the schema lookup (the last schema, then the cache) and the decode, which a stream of
/// messages repeats per message. The objects are small, so the lookup's cost shows. The registry reader is the same
/// path for Confluent framing.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MessageBenchmarks
{
    private const string UserJson = """
        {"type":"record","name":"User","namespace":"bench.messages","fields":[{"name":"id","type":"long"},{"name":"name","type":"string"}]}
        """;

    private AvroMessageReader<AvroValue> _genericReader = null!;
    private AvroMessageReader<bench.generated.Order> _generatedReader = null!;
    private AvroRegistryMessageReader<AvroValue> _registryReader = null!;
    private byte[] _genericMessage = [];
    private byte[] _generatedMessage = [];
    private byte[] _registryMessage = [];

    [GlobalSetup]
    public void Setup()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserJson);
        var user = new GenericRecord(schema) { ["id"] = 1_790_000_000_123L, ["name"] = "Customer 42" };
        var writer = GenericDatumWriter.Create(schema);

        _genericReader = AvroMessageReader.CreateGeneric(new AvroSchemaStore(schema));
        _genericMessage = AvroMessage.ToArray(user, writer);

        var order = new bench.generated.Order { Id = 42, Customer = "Customer 42", Total = 9.5, Quantity = 1, Paid = true, Tags = new Dictionary<string, string>(StringComparer.Ordinal) };
        _generatedReader = AvroMessageReader.Create<bench.generated.Order>(new AvroSchemaStore(bench.generated.Order.Schema), _ => bench.generated.Order.Read);
        _generatedMessage = AvroMessage.ToArray(order, bench.generated.Order.Schema, bench.generated.Order.Write);

        var id = AvroSchemaId.FromNumber(7);
        var ids = new AvroSchemaIdStore();
        ids.Add(id, schema);
        _registryReader = AvroRegistryMessageReader.CreateGeneric(AvroRegistryFraming.Confluent, ids);
        _registryMessage = AvroRegistryMessage.ToArray(AvroRegistryFraming.Confluent, id, user, writer);

        // Each reader must read back what was written before its time means anything.
        if (!_genericReader.Read(_genericMessage).Equals((AvroValue)user)
            || !_registryReader.Read(_registryMessage).Equals((AvroValue)user)
            || !_generatedReader.Read(_generatedMessage).ToAvroBytes().AsSpan().SequenceEqual(order.ToAvroBytes()))
        {
            throw new InvalidOperationException("A message reader did not read back what was written.");
        }
    }

    [Benchmark]
    [BenchmarkCategory("SingleObject")]
    public AvroValue SingleObject_Generic_Read() => _genericReader.Read(_genericMessage);

    [Benchmark]
    [BenchmarkCategory("SingleObject")]
    public bench.generated.Order SingleObject_Generated_Read() => _generatedReader.Read(_generatedMessage);

    [Benchmark]
    [BenchmarkCategory("Registry")]
    public AvroValue Registry_Generic_Read() => _registryReader.Read(_registryMessage);
}
