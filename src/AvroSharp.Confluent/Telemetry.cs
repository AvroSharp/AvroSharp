using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// The package's metrics and traces, for OpenTelemetry: a <see cref="Meter"/> and an <see cref="ActivitySource"/>
/// named <c>AvroSharp.Confluent</c>. Nothing is measured unless a listener subscribes, so the cost to a message is a
/// check of each instrument's <c>Enabled</c>.
/// </summary>
internal static class Telemetry
{
    public const string Name = "AvroSharp.Confluent";

    private static readonly string? Version = typeof(Telemetry).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource Source = new(Name, Version);

    public static readonly Meter Meter = new(Name, Version);

    /// <summary>Calls to the registry client when the serde's own caches miss (the client can still answer from its cache).</summary>
    public static readonly Counter<long> RegistryCalls = Meter.CreateCounter<long>(
        "avrosharp.confluent.registry.calls", "{call}", "Calls to the schema registry client that the serde's caches missed, by operation: register, lookup, get_schema, get_schema_by_guid, migrations.");

    /// <summary>Messages that failed to serialize or deserialize.</summary>
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "avrosharp.confluent.serde.errors", "{message}", "Messages that failed to serialize or deserialize, by error.type.");

    /// <summary>Messages whose data contract rules failed: a subset of <see cref="Errors"/>.</summary>
    public static readonly Counter<long> RuleFailures = Meter.CreateCounter<long>(
        "avrosharp.confluent.rule.failures", "{message}", "Messages whose data contract rules failed or whose conditions were false.");

    /// <summary>The time a message takes to serialize or deserialize, registry calls included.</summary>
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "avrosharp.confluent.serde.duration", "s", "The time to serialize or deserialize a message, registry calls included.");

    /// <summary>Whether a message's outcome is measured: its time or its failure.</summary>
    public static bool Measuring => Duration.Enabled || Errors.Enabled || RuleFailures.Enabled;

    /// <summary>Counts a registry call, and starts its activity (<see langword="null"/> without a listener).</summary>
    public static Activity? RegistryCall(string operation, string? subject)
    {
        RegistryCalls.Add(1, new KeyValuePair<string, object?>("avrosharp.operation", operation), new KeyValuePair<string, object?>("avrosharp.subject", subject));
        var activity = Source.StartActivity("schema_registry " + operation, ActivityKind.Client);
        activity?.SetTag("avrosharp.operation", operation);
        activity?.SetTag("avrosharp.subject", subject);
        return activity;
    }

    /// <summary>Records a message's time, from <paramref name="start"/>, or its failure.</summary>
    public static void Record(string serde, SerializationContext context, long start, Exception? error)
    {
        var tags = new TagList
        {
            { "avrosharp.serde", serde },
            { "messaging.destination.name", context.Topic },
            { "avrosharp.component", context.Component == MessageComponentType.Key ? "key" : "value" },
        };

        if (error is not null)
        {
            tags.Add("error.type", error.GetType().FullName);
            Errors.Add(1, tags);
            if (error is RuleException)
            {
                RuleFailures.Add(1, tags);
            }
        }

        if (Duration.Enabled)
        {
            Duration.Record(Elapsed(start), tags);
        }
    }

    /// <summary>Records the outcome of a message's task, once it completes.</summary>
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The task is the serde's own, started by the caller for this message.")]
    public static async Task<TResult> ObserveAsync<TResult>(Task<TResult> task, string serde, SerializationContext context, long start)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            Record(serde, context, start, null);
            return result;
        }
        catch (Exception e)
        {
            Record(serde, context, start, e);
            throw;
        }
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();

    private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
}
