// C# types that the AvroSharp.Generators package generates from Schemas/*.avsc, in the namespaces that the project's
// AvroSharpNamespace and AvroSharpNamespaceMap choose.
using System;
using AvroSharp.Schemas;
using Shop;
using Shop.Customers;
using Shop.Orders;

// order.avsc refers to com.example.crm.Customer, which customer.avsc defines: the generator reads every schema file
// together, so a file can use the types of the others.
var order = new Order
{
    OrderId = Guid.NewGuid(),
    Placed = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
    Total = 129.95m,
    Customer = new Customer { CustomerId = 42, DisplayName = "Ada" },
};

byte[] bytes = order.ToAvroBytes();
Order copy = Order.FromAvroBytes(bytes);
Console.WriteLine($"Order: {bytes.Length} bytes, customer {copy.Customer.DisplayName}, total {copy.Total}");
Check(copy.OrderId == order.OrderId && copy.Placed == order.Placed && copy.Total == order.Total
    && copy.Customer.CustomerId == 42 && copy.Note is null);

// heartbeat.avsc has no Avro namespace, so its type is in AvroSharpNamespace.
var heartbeat = new Heartbeat { ServiceName = "checkout", SentAt = order.Placed };
Heartbeat heartbeatCopy = Heartbeat.FromAvroBytes(heartbeat.ToAvroBytes());
Check(string.Equals(heartbeatCopy.ServiceName, "checkout", StringComparison.Ordinal) && heartbeatCopy.SentAt == order.Placed);

// Only the C# namespaces change: the schemas keep their Avro names, so the data and fingerprints stay the same.
Console.WriteLine($"Schemas: {FullName(Order.Schema)}, {FullName(Customer.Schema)}, {FullName(Heartbeat.Schema)}");
Check(string.Equals(FullName(Order.Schema), "com.example.shop.Order", StringComparison.Ordinal)
    && string.Equals(FullName(Customer.Schema), "com.example.crm.Customer", StringComparison.Ordinal));

Console.WriteLine("OK");
return 0;

static string FullName(AvroSchema schema) => ((NamedSchema)schema).FullName;

static void Check(bool condition)
{
    if (!condition)
    {
        Console.WriteLine("FAILED");
        Environment.Exit(1);
    }
}
