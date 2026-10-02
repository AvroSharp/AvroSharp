using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AvroSharp.Schemas;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Values in Apache.Avro's generic model (a <c>GenericRecord</c>, a <c>GenericEnum</c>, ...), decoded from and encoded
/// to Avro binary, for Confluent's CEL executor. It reads a value by its Avro field names only when the value is one of
/// Apache.Avro's records; anything else it reads by the .NET type's property names (#191).
/// </summary>
/// <remarks>
/// This package doesn't reference Apache.Avro, so it is reached by reflection. Confluent.SchemaRegistry.Rules, which has
/// the CEL executor, depends on Apache.Avro, so it is there whenever CEL rules can run.
/// </remarks>
internal static class ApacheAvroGeneric
{
    private const string Justification =
        "Used only for CEL rules, which Confluent.SchemaRegistry.Rules runs with Apache.Avro and reflection; neither is trim- or AOT-compatible, so an application that runs CEL rules can't be trimmed anyway.";

    private static readonly Lazy<Api?> Loaded = new(Load);

    // Apache.Avro's schema for each AvroSharp schema, parsed from its JSON (references included).
    private static readonly ConditionalWeakTable<AvroSchema, object> Schemas = new();

    /// <summary>Gets whether Apache.Avro is loaded, or can be.</summary>
    public static bool IsAvailable => Loaded.Value is not null;

    /// <summary>
    /// Gets whether the domain rules include CEL rules, which then see the value as Apache.Avro's generic model, by its
    /// Avro field names.
    /// </summary>
    public static bool HasCelRules(RuleSet? rules) =>
        rules?.DomainRules?.Any(rule => string.Equals(rule.Type, "CEL", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Gets whether a rule's result must be written again: when the domain rules include a transform, which may return
    /// another value or change the one it was given. Conditions leave the value as it is.
    /// </summary>
    public static bool HasTransforms(RuleSet rules) => rules.DomainRules.Any(rule => rule.Kind == RuleKind.Transform);

    /// <summary>Decodes data written with <paramref name="writer"/> as a value of <paramref name="reader"/>.</summary>
    public static object? Decode(AvroSchema writer, AvroSchema reader, ReadOnlyMemory<byte> data)
    {
        var api = Loaded.Value!;
        using var stream = MemoryMarshal.TryGetArray(data, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
        var datumReader = Invoke(() => api.Reader.Invoke([ApacheSchema(api, writer), ApacheSchema(api, reader)]));
        var decoder = Invoke(() => api.Decoder.Invoke([stream]));
        return Invoke(() => api.Read.Invoke(datumReader, [null, decoder]));
    }

    /// <summary>Encodes a value of Apache.Avro's generic model with <paramref name="schema"/>.</summary>
    /// <exception cref="InvalidOperationException">The value isn't one of <paramref name="schema"/>.</exception>
    public static ReadOnlyMemory<byte> Encode(AvroSchema schema, object? value)
    {
        var api = Loaded.Value!;
        using var stream = new MemoryStream();
        var datumWriter = Invoke(() => api.Writer.Invoke([ApacheSchema(api, schema)]));
        var encoder = Invoke(() => api.Encoder.Invoke([stream]));
        try
        {
            Invoke(() => api.Write.Invoke(datumWriter, [value, encoder]));
        }
        catch (Exception e) when (e.GetType().FullName?.StartsWith("Avro.", StringComparison.Ordinal) == true || e is InvalidCastException)
        {
            throw new InvalidOperationException(
                $"A rule returned a value that isn't one of the schema '{(schema as NamedSchema)?.FullName ?? schema.Type.ToString()}' ({value?.GetType().Name ?? "null"}), so it can't be written.", e);
        }

        api.Flush.Invoke(encoder, null);
        return new ReadOnlyMemory<byte>(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static object ApacheSchema(Api api, AvroSchema schema) =>
        Schemas.GetValue(schema, s => Invoke(() => api.Parse.Invoke(null, [s.ToJson()]))!);

    // The exception a reflected call threw, rather than the TargetInvocationException around it.
    private static object? Invoke(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = Justification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = Justification)]
    private static Api? Load()
    {
        var schema = Type.GetType("Avro.Schema, Avro");
        if (schema is null)
        {
            return null;
        }

        var assembly = schema.Assembly;
        var reader = assembly.GetType("Avro.Generic.GenericDatumReader`1", throwOnError: true)!.MakeGenericType(typeof(object));
        var writer = assembly.GetType("Avro.Generic.GenericDatumWriter`1", throwOnError: true)!.MakeGenericType(typeof(object));
        var decoder = assembly.GetType("Avro.IO.BinaryDecoder", throwOnError: true)!;
        var encoder = assembly.GetType("Avro.IO.BinaryEncoder", throwOnError: true)!;
        var decoderBase = assembly.GetType("Avro.IO.Decoder", throwOnError: true)!;
        var encoderBase = assembly.GetType("Avro.IO.Encoder", throwOnError: true)!;
        return new Api(
            schema.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null)!,
            reader.GetConstructor([schema, schema])!,
            reader.GetMethod("Read", [typeof(object), decoderBase])!,
            decoder.GetConstructor([typeof(Stream)])!,
            writer.GetConstructor([schema])!,
            writer.GetMethod("Write", [typeof(object), encoderBase])!,
            encoder.GetConstructor([typeof(Stream)])!,
            encoder.GetMethod("Flush", Type.EmptyTypes)!);
    }

    private sealed record Api(
        MethodInfo Parse,
        ConstructorInfo Reader,
        MethodInfo Read,
        ConstructorInfo Decoder,
        ConstructorInfo Writer,
        MethodInfo Write,
        ConstructorInfo Encoder,
        MethodInfo Flush);
}
