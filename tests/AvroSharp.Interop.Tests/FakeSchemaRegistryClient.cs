using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Confluent.SchemaRegistry;

namespace AvroSharp.Interop.Tests;

/// <summary>
/// A registry client that knows one schema under one ID. The serializer and deserializer call only a few members;
/// any other call fails with its name, so a change in Confluent's client shows up here.
/// </summary>
public class FakeSchemaRegistryClient : DispatchProxy
{
    private int _id;
    private string _schema = string.Empty;

    public static ISchemaRegistryClient Create(int id, string schema)
    {
        var client = Create<ISchemaRegistryClient, FakeSchemaRegistryClient>();
        var fake = (FakeSchemaRegistryClient)(object)client;
        fake._id = id;
        fake._schema = schema;
        return client;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("No method.");
        var returns = method.ReturnType;
        if (returns == typeof(Task<int>))
        {
            return Task.FromResult(_id);
        }

        if (returns == typeof(Task<Schema>))
        {
            return Task.FromResult(new Schema(_schema, SchemaType.Avro));
        }

        if (returns == typeof(Task<RegisteredSchema>))
        {
            return Task.FromResult(new RegisteredSchema("users-value", 1, _id, _schema, SchemaType.Avro, new List<SchemaReference>()));
        }

        // Lookups that return lists (for example schema associations in newer clients) find nothing.
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>)
            && returns.GetGenericArguments()[0] is { IsGenericType: true } listType
            && listType.GetGenericTypeDefinition() is var definition
            && (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>)))
        {
            var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(listType.GetGenericArguments()[0]));
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(listType).Invoke(null, [empty]);
        }

        if (returns == typeof(int))
        {
            return 1000;
        }

        if (string.Equals(method.Name, "ConstructValueSubjectName", StringComparison.Ordinal) || string.Equals(method.Name, "ConstructKeySubjectName", StringComparison.Ordinal))
        {
            return args?[0] + (method.Name.Contains("Value", StringComparison.Ordinal) ? "-value" : "-key");
        }

        if (returns == typeof(void))
        {
            return null;
        }

        throw new NotSupportedException($"The fake registry does not implement {method.Name}.");
    }
}
