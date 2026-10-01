using System;
using System.Threading.Tasks;
using Confluent.SchemaRegistry;
using Testcontainers.Redpanda;
using TUnit.Core.Interfaces;

namespace AvroSharp.Confluent.Tests;

/// <summary>One Redpanda container for a test class (also used by AvroSharp.KafkaFlow.Tests).</summary>
public sealed class RedpandaFixture : IAsyncInitializer, IAsyncDisposable
{
    private RedpandaContainer? _container;

    public string BootstrapServers => Container.GetBootstrapAddress();

    private RedpandaContainer Container => _container ?? throw new InvalidOperationException("The container hasn't started.");

    public string SchemaRegistryAddress => Container.GetSchemaRegistryAddress();

    public CachedSchemaRegistryClient Registry() => new(new SchemaRegistryConfig { Url = SchemaRegistryAddress });

    // Building a container already looks for Docker, so it's built here, when the tests run, not when the class loads.
    // The image's default (v22) predates the registry API that Confluent.SchemaRegistry 2.15 uses.
    public Task InitializeAsync()
    {
        _container = new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v25.2.1").Build();
        return _container.StartAsync();
    }

    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? default;
}

