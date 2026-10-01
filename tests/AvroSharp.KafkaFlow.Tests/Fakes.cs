using System;
using System.Collections.Generic;
using Confluent.SchemaRegistry;
using KafkaFlow;

namespace AvroSharp.KafkaFlow.Tests;

/// <summary>KafkaFlow's dependency resolver, with only the schema registry, as <c>WithSchemaRegistry</c> registers it.</summary>
internal sealed class RegistryResolver(ISchemaRegistryClient? registry) : IDependencyResolver
{
    public object Resolve(Type type) => type == typeof(ISchemaRegistryClient) ? registry! : null!;

    public IEnumerable<object> ResolveAll(Type type) => [Resolve(type)];

    public IDependencyResolverScope CreateScope() => throw new NotSupportedException();
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
