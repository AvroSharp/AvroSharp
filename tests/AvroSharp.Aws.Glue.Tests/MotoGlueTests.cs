using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Glue;
using Amazon.Glue.Model;
using Amazon.Runtime;
using AvroSharp.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using test.glue;
using Order = test.glue.Order;
using TUnit.Core.Interfaces;

namespace AvroSharp.Aws.Glue.Tests;

/// <summary>
/// Against moto, an emulator of the AWS APIs with Glue Schema Registry, in Docker, through the AWS SDK's real client:
/// the requests the package makes are the ones the service takes. They need Docker with Linux containers, so they run
/// only when a filter names them:
/// dotnet test --project tests/AvroSharp.Aws.Glue.Tests --treenode-filter "/*/*/MotoGlueTests/*".
/// </summary>
[Explicit]
[NotInParallel]
[ClassDataSource<MotoFixture>(Shared = SharedType.PerClass)]
public class MotoGlueTests(MotoFixture moto)
{
    [Test]
    public async Task AutoRegistration_CreatesTheSchema_AndReusesItsVersion()
    {
        using var glue = moto.Client();
        var registry = "registry-" + Guid.NewGuid().ToString("N")[..8];
        await glue.CreateRegistryAsync(new CreateRegistryRequest { RegistryName = registry });
        var options = new AvroSharpGlueOptions { AutoRegisterSchemas = true, RegistryName = registry, PendingVersionInterval = TimeSpan.FromMilliseconds(200) };
        var writer = new AvroSharpGlueSerializer(glue, options);

        var item = await writer.SerializeAsync(new Item { Sku = "SKU-1", Quantity = 1 }, "items");
        var compressed = await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { AutoRegisterSchemas = true, RegistryName = registry, Compression = AvroSharpGlueCompression.Zlib })
            .SerializeAsync(new Item { Sku = "SKU-2", Quantity = 2 }, "items");
        var schema = await glue.GetSchemaAsync(new GetSchemaRequest { SchemaId = new SchemaId { RegistryName = registry, SchemaName = "items" } });
        var reader = new AvroSharpGlueSerializer(glue);

        await Assert.That(schema.LatestSchemaVersion).IsEqualTo(1);
        await Assert.That((await reader.DeserializeAsync<Item>(item)).Sku).IsEqualTo("SKU-1");
        await Assert.That((await reader.DeserializeAsync<Item>(compressed)).Quantity).IsEqualTo(2);
    }

    [Test]
    public async Task AVersionRegisteredElsewhere_IsFoundByItsDefinition()
    {
        using var glue = moto.Client();
        var registry = "registry-" + Guid.NewGuid().ToString("N")[..8];
        await glue.CreateRegistryAsync(new CreateRegistryRequest { RegistryName = registry });
        // As AWS's Java or native serializer registers it: Java's Schema.toString() text, which ToJson() is.
        var created = await glue.CreateSchemaAsync(new CreateSchemaRequest
        {
            RegistryId = new RegistryId { RegistryName = registry },
            SchemaName = "orders",
            DataFormat = DataFormat.AVRO,
            Compatibility = Compatibility.BACKWARD,
            SchemaDefinition = AvroTypes.Get<Order>().Schema.ToJson(),
        });

        var message = await new AvroSharpGlueSerializer(glue, new AvroSharpGlueOptions { RegistryName = registry })
            .SerializeAsync(new Order { Id = Guid.NewGuid(), Customer = "Ada", Total = new Avro.AvroDecimal(1.50m), Items = new List<Item>() }, "orders");
        var read = await new AvroSharpGlueSerializer(glue).DeserializeAsync<Order>(message);

        await Assert.That(GlueSerializerTests.BigEndianGuid(message.AsSpan(2, 16))).IsEqualTo(Guid.Parse(created.SchemaVersionId));
        await Assert.That(read.Customer).IsEqualTo("Ada");
    }
}

/// <summary>One moto container for the test class.</summary>
public sealed class MotoFixture : IAsyncInitializer, IAsyncDisposable
{
    private IContainer? _container;

    public AmazonGlueClient Client() => new(
        new BasicAWSCredentials("test", "test"),
        new AmazonGlueConfig { ServiceURL = $"http://{_container!.Hostname}:{_container.GetMappedPublicPort(5000)}", AuthenticationRegion = "us-east-1" });

    // Built here, when the tests run: building a container already looks for Docker.
    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("motoserver/moto:5.2.3")
            .WithPortBinding(5000, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(5000).ForPath("/moto-api/")))
            .Build();
        await _container.StartAsync();
    }

    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? default;
}
