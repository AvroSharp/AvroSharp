using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using KafkaFlow;
using Shop.Events;

// KafkaFlow creates the handlers, with their dependencies from the service collection.
namespace Shop.Billing;

/// <summary>What the handlers received, in order.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "KafkaFlow and the service collection create it.")]
internal sealed class Received
{
    public ConcurrentQueue<object> Messages { get; } = new();
}

/// <summary>Handles the orders placed: KafkaFlow calls it for messages read as <see cref="OrderPlaced"/>.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "KafkaFlow and the service collection create it.")]
internal sealed class OrderPlacedHandler(Received received) : IMessageHandler<OrderPlaced>
{
    public Task Handle(IMessageContext context, OrderPlaced message)
    {
        Console.WriteLine($"Placed: order {message.OrderId} by {message.Customer}, {message.Total}");
        received.Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Handles the orders shipped.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "KafkaFlow and the service collection create it.")]
internal sealed class OrderShippedHandler(Received received) : IMessageHandler<OrderShipped>
{
    public Task Handle(IMessageContext context, OrderShipped message)
    {
        Console.WriteLine($"Shipped: order {message.OrderId} with {message.Carrier}, tracking {message.TrackingNumber}");
        received.Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
