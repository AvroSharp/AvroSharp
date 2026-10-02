using System;
using System.Collections.Generic;
using Confluent.SchemaRegistry;
using KafkaFlow;
using KafkaFlow.Configuration;

namespace AvroSharp.KafkaFlow.Tests;

/// <summary>KafkaFlow's dependency resolver, with only the schema registry, as <c>WithSchemaRegistry</c> registers it.</summary>
internal sealed class RegistryResolver(ISchemaRegistryClient? registry) : IDependencyResolver
{
    public object Resolve(Type type) => type == typeof(ISchemaRegistryClient) ? registry! : null!;

    public IEnumerable<object> ResolveAll(Type type) => [Resolve(type)];

    public IDependencyResolverScope CreateScope() => throw new NotSupportedException();
}

/// <summary>A consumer's middlewares: keeps the factories added, to create the middlewares later.</summary>
internal sealed class ConsumerMiddlewares : IConsumerMiddlewareConfigurationBuilder
{
    public List<Func<IDependencyResolver, IMessageMiddleware>> Factories { get; } = [];

    public IDependencyConfigurator DependencyConfigurator => throw new NotSupportedException();

    public IConsumerMiddlewareConfigurationBuilder Add<T>(Factory<T> factory, MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware
    {
        Factories.Add(resolver => factory(resolver));
        return this;
    }

    public IConsumerMiddlewareConfigurationBuilder Add<T>(MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();

    public IConsumerMiddlewareConfigurationBuilder AddAtBeginning<T>(Factory<T> factory, MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();

    public IConsumerMiddlewareConfigurationBuilder AddAtBeginning<T>(MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();
}

/// <summary>A producer's middlewares: keeps the factories added, to create the middlewares later.</summary>
internal sealed class ProducerMiddlewares : IProducerMiddlewareConfigurationBuilder
{
    public List<Func<IDependencyResolver, IMessageMiddleware>> Factories { get; } = [];

    public IDependencyConfigurator DependencyConfigurator => throw new NotSupportedException();

    public IProducerMiddlewareConfigurationBuilder Add<T>(Factory<T> factory, MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware
    {
        Factories.Add(resolver => factory(resolver));
        return this;
    }

    public IProducerMiddlewareConfigurationBuilder Add<T>(MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();

    public IProducerMiddlewareConfigurationBuilder AddAtBeginning<T>(Factory<T> factory, MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();

    public IProducerMiddlewareConfigurationBuilder AddAtBeginning<T>(MiddlewareLifetime lifetime = MiddlewareLifetime.ConsumerOrProducer)
        where T : class, IMessageMiddleware => throw new NotSupportedException();
}

/// <summary>A consumed message, for the type resolver, which reads only its value.</summary>
internal sealed class ConsumedMessage(byte[] value) : IMessageContext
{
    public Message Message { get; } = new(null!, value);

    public IMessageHeaders Headers { get; } = new MessageHeaders();

    public IConsumerContext ConsumerContext => throw new NotSupportedException();

    public IProducerContext ProducerContext => throw new NotSupportedException();

    public IReadOnlyCollection<string> Brokers => [];

    public IDependencyResolver DependencyResolver => throw new NotSupportedException();

    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    public IMessageContext SetMessage(object key, object value) => throw new NotSupportedException();
}
