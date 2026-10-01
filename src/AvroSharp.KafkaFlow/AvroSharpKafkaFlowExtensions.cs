using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using AvroSharp.Confluent;
using Confluent.SchemaRegistry;
using global::KafkaFlow;
using global::KafkaFlow.Configuration;
using global::KafkaFlow.Middlewares.Serializer;
using global::KafkaFlow.Middlewares.Serializer.Resolvers;

namespace AvroSharp.KafkaFlow;

/// <summary>
/// Adds AvroSharp's Confluent Schema Registry serializer and deserializers to KafkaFlow producers and consumers, in
/// place of KafkaFlow's <c>AddSchemaRegistryAvroSerializer</c> and <c>AddSchemaRegistryAvroDeserializer</c>. The
/// registry is the one set with KafkaFlow's <c>WithSchemaRegistry</c> on the cluster.
/// </summary>
public static class AvroSharpKafkaFlowExtensions
{
    /// <summary>Writes each produced message with <see cref="AvroSharpKafkaFlowSerializer"/>: of any type <see cref="Serialization.AvroTypes"/> knows.</summary>
    /// <param name="middlewares">The producer's middlewares.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults. The schema ID can't go in a header.</param>
    public static IProducerMiddlewareConfigurationBuilder AddSchemaRegistryAvroSharpSerializer(this IProducerMiddlewareConfigurationBuilder middlewares, AvroSharpSerializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(middlewares);
        return middlewares.Add(resolver => new SerializerProducerMiddleware(new AvroSharpKafkaFlowSerializer(Registry(resolver), config), ProducedType.Instance));
    }

    /// <summary>Reads each consumed message as <typeparamref name="TMessage"/>, whatever version of its schema wrote it.</summary>
    /// <typeparam name="TMessage">The message type, which <see cref="Serialization.AvroTypes"/> knows.</typeparam>
    /// <param name="middlewares">The consumer's middlewares.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static IConsumerMiddlewareConfigurationBuilder AddSchemaRegistryAvroSharpDeserializer<TMessage>(this IConsumerMiddlewareConfigurationBuilder middlewares, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(middlewares);
        return middlewares.Add(resolver => new DeserializerConsumerMiddleware(new AvroSharpKafkaFlowDeserializer(Registry(resolver), config), new SingleMessageTypeResolver(typeof(TMessage))));
    }

    /// <summary>
    /// Reads each consumed message as the one of <paramref name="messageTypes"/> whose record its writer's schema is,
    /// for topics with several record types (<see cref="AvroSharpMessageTypeResolver"/>).
    /// </summary>
    /// <param name="middlewares">The consumer's middlewares.</param>
    /// <param name="messageTypes">The message types, which <see cref="Serialization.AvroTypes"/> knows, each of a record schema.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    public static IConsumerMiddlewareConfigurationBuilder AddSchemaRegistryAvroSharpDeserializer(this IConsumerMiddlewareConfigurationBuilder middlewares, IEnumerable<Type> messageTypes, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(middlewares);
        ArgumentNullException.ThrowIfNull(messageTypes);
        var types = new List<Type>(messageTypes);
        return middlewares.Add(resolver =>
        {
            var registry = Registry(resolver);
            return new DeserializerConsumerMiddleware(new AvroSharpKafkaFlowDeserializer(registry, config), new AvroSharpMessageTypeResolver(registry, types));
        });
    }

    /// <summary>
    /// Reads each consumed message as the type its writer's record names, found by its .NET full name in the loaded
    /// assemblies, as KafkaFlow's <c>AddSchemaRegistryAvroDeserializer</c> does.
    /// </summary>
    /// <param name="middlewares">The consumer's middlewares.</param>
    /// <param name="config">The settings, or <see langword="null"/> for the defaults.</param>
    [RequiresUnreferencedCode("The message types are found by name in the loaded assemblies, and trimming can remove them. Pass the message types instead.")]
    public static IConsumerMiddlewareConfigurationBuilder AddSchemaRegistryAvroSharpDeserializer(this IConsumerMiddlewareConfigurationBuilder middlewares, AvroSharpDeserializerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(middlewares);
        return middlewares.Add(resolver =>
        {
            var registry = Registry(resolver);
            return new DeserializerConsumerMiddleware(new AvroSharpKafkaFlowDeserializer(registry, config), new AvroSharpMessageTypeResolver(registry));
        });
    }

    private static ISchemaRegistryClient Registry(IDependencyResolver resolver) =>
        resolver.Resolve<ISchemaRegistryClient>()
        ?? throw new InvalidOperationException("No schema registry is configured: set one with WithSchemaRegistry on the cluster.");

    // A produced message's type is its value's: nothing to resolve or to put in a header.
    private sealed class ProducedType : IMessageTypeResolver
    {
        public static readonly ProducedType Instance = new();

        public ValueTask<Type> OnConsumeAsync(IMessageContext context) => throw new NotSupportedException();

        public ValueTask OnProduceAsync(IMessageContext context) => default;
    }
}
