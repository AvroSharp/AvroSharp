using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using aot.shop;
using AvroSharp.Aws.Glue;
using AvroSharp.Aws.Glue.Kafka;
using AvroSharp.Aws.Glue.Tests;
using AvroSharp.Azure.SchemaRegistry;
using AvroSharp.Azure.SchemaRegistry.Tests;
using AvroSharp.Confluent;
using AvroSharp.Confluent.Tests;
using AvroSharp.AotSmoke.Addons;
using AvroSharp.Generic;
using Azure.Messaging;
using Confluent.Kafka;

// Native AOT smoke test of the add-on packages: each writes and reads an Order through its registry client, against
// the in-memory registries of the tests. The exit code is the result.
var failures = 0;

void Check(bool condition, string what)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAIL: {what}");
        failures++;
    }
}

bool IsOrder(Order? order) =>
    order is { Id: 7, Customer: "Ada", Total: 129.95m } && order.Lines.Count == 2 && string.Equals(order.Lines[1], "SKU-2", StringComparison.Ordinal);

Order NewOrder() => new() { Id = 7, Customer = "Ada", Total = 129.95m, Lines = new List<string> { "SKU-1", "SKU-2" } };

await Run("Confluent", async () =>
{
    using var registry = new InMemorySchemaRegistry();
    var context = new SerializationContext(MessageComponentType.Value, "orders");
    var serializer = new AvroSharpSerializer<Order>(registry);
    var deserializer = new AvroSharpDeserializer<Order>(registry);

    var message = await serializer.SerializeAsync(NewOrder(), context);
    Check(IsOrder(await deserializer.DeserializeAsync(message, isNull: false, context)), "Confluent: async round trip");
    Check(IsOrder(deserializer.Deserialize(serializer.Serialize(NewOrder(), context), isNull: false, context)), "Confluent: sync round trip");

    var generic = AvroSharpGeneric.CreateSerializer(registry);
    var record = (await AvroSharpGeneric.CreateDeserializer(registry).DeserializeAsync(message, isNull: false, context)).AsRecord();
    var again = await generic.SerializeAsync(AvroValue.FromRecord(record), context);
    Check(IsOrder(await deserializer.DeserializeAsync(again, isNull: false, context)), "Confluent: generic records");
});

await Run("Azure", async () =>
{
    var registry = new InMemorySchemaRegistryClient();
    var serializer = new AvroSharpSchemaRegistrySerializer(registry, "orders", new AvroSharpSchemaRegistrySerializerOptions { AutoRegisterSchemas = true });

    var message = await serializer.SerializeAsync<MessageContent, Order>(NewOrder());
    Check(message.ContentType?.ToString().StartsWith("avro/binary+", StringComparison.Ordinal) == true, "Azure: content type");
    Check(IsOrder(await serializer.DeserializeAsync<Order>(message)), "Azure: round trip");
});

await Run("AWS Glue", async () =>
{
    using var glue = new InMemoryGlueClient();
    var options = new AvroSharpGlueOptions { AutoRegisterSchemas = true };
    var serializer = new AvroSharpGlueSerializer(glue, options);
    var context = new SerializationContext(MessageComponentType.Value, "orders");

    var message = await serializer.SerializeAsync(NewOrder(), "orders");
    Check(IsOrder(await serializer.DeserializeAsync<Order>(message)), "Glue: round trip");
    var kafka = new AvroSharpGlueKafkaSerializer<Order>(serializer).Serialize(NewOrder(), context);
    Check(IsOrder(new AvroSharpGlueKafkaDeserializer<Order>(serializer).Deserialize(kafka, isNull: false, context)), "Glue: Kafka adapters");
});

// The real registry clients, created but never called (no network), so the trimmer analyzes the code an application
// reaches: their own trim and AOT warnings are the ones to document.
await Run("real clients", () =>
{
    using var confluent = new global::Confluent.SchemaRegistry.CachedSchemaRegistryClient(new global::Confluent.SchemaRegistry.SchemaRegistryConfig { Url = "http://127.0.0.1:1" });
    _ = new AvroSharpSerializer<Order>(confluent);
    var azure = new global::Azure.Data.SchemaRegistry.SchemaRegistryClient("127.0.0.1", new NoCredential());
    _ = new AvroSharpSchemaRegistrySerializer(azure, "orders");
    using var glue = new global::Amazon.Glue.AmazonGlueClient(new global::Amazon.Runtime.BasicAWSCredentials("aot", "aot"), global::Amazon.RegionEndpoint.USEast1);
    _ = new AvroSharpGlueSerializer(glue);
    return Task.CompletedTask;
});

Console.WriteLine(failures == 0 ? "AOT add-ons smoke test passed." : $"AOT add-ons smoke test: {failures} failure(s).");
return failures == 0 ? 0 : 1;

async Task Run(string name, Func<Task> body)
{
    try
    {
        await body();
    }
#pragma warning disable CA1031 // A smoke test reports whatever fails, and goes on to the next package.
    catch (Exception ex)
#pragma warning restore CA1031
    {
        Console.Error.WriteLine($"FAIL: {name} threw {ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

