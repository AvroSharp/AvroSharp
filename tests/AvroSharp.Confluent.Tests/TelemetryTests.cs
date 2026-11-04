using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using test.shop;
using static AvroSharp.Confluent.Tests.TestData;

namespace AvroSharp.Confluent.Tests;

/// <summary>
/// The metrics and traces of the <c>AvroSharp.Confluent</c> meter and activity source, as an OpenTelemetry exporter
/// sees them. Tests run in parallel, so each measurement is matched to this test's topic or subject.
/// </summary>
public class TelemetryTests
{
    [Test]
    public async Task AListener_SeesRegistryCalls_Durations_AndErrors()
    {
        var topic = Topic();
        var subject = topic + "-value";
        using var recorder = new Recorder();

        using var registry = new InMemorySchemaRegistry();
        var serializer = new AvroSharpSerializer<Order>(registry);
        var deserializer = new AvroSharpDeserializer<Order>(registry, new AvroSharpDeserializerConfig { FailOnTrailingData = true });
        var message = (await serializer.SerializeAsync(NewOrder(), Value(topic)))!;
        serializer.Serialize(NewOrder(), Value(topic));
        await deserializer.DeserializeAsync(message, isNull: false, Value(topic));
        deserializer.Deserialize(message, isNull: false, Value(topic));
        var extended = message.Append((byte)0).ToArray();
        await Assert.That(async () => await deserializer.DeserializeAsync(extended, isNull: false, Value(topic))).Throws<InvalidDataException>();
        await Assert.That(() => deserializer.Deserialize(extended, isNull: false, Value(topic))).Throws<InvalidDataException>();

        var ours = recorder.Measurements.Where(m => m.Is("messaging.destination.name", topic) || m.Is("avrosharp.subject", subject)).ToList();
        var calls = ours.Where(m => m.Named("avrosharp.confluent.registry.calls")).Select(m => m.Tag("avrosharp.operation")).ToList();
        await Assert.That(calls).Contains("register");
        await Assert.That(calls).Contains("get_schema");

        // Each message is measured once: 2 writes and 4 reads, of which 2 failed.
        var durations = ours.Where(m => m.Named("avrosharp.confluent.serde.duration")).ToList();
        await Assert.That(durations.Count(m => m.Is("avrosharp.serde", "serializer"))).IsEqualTo(2);
        await Assert.That(durations.Count(m => m.Is("avrosharp.serde", "deserializer"))).IsEqualTo(4);
        await Assert.That(durations.All(m => m.Value >= 0 && m.Is("avrosharp.component", "value"))).IsTrue();
        var errors = ours.Where(m => m.Named("avrosharp.confluent.serde.errors")).ToList();
        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors.All(m => m.Is("error.type", typeof(InvalidDataException).FullName!))).IsTrue();
        await Assert.That(ours.Any(m => m.Named("avrosharp.confluent.rule.failures"))).IsFalse();
        await Assert.That(recorder.Activities.Any(a => string.Equals(a.DisplayName, "schema_registry register", StringComparison.Ordinal)
            && string.Equals(a.GetTagItem("avrosharp.subject") as string, subject, StringComparison.Ordinal))).IsTrue();
    }

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags)
    {
        public bool Named(string instrument) => string.Equals(Instrument, instrument, StringComparison.Ordinal);

        public string? Tag(string key) => Tags.GetValueOrDefault(key) as string;

        public bool Is(string key, string value) => string.Equals(Tag(key), value, StringComparison.Ordinal);
    }

    // Listens to the package's meter and activity source while it is alive.
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _meters;
        private readonly ActivityListener _tracing;

        public Recorder()
        {
            _meters = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (string.Equals(instrument.Meter.Name, "AvroSharp.Confluent", StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _meters.Start();
            _tracing = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(source.Name, "AvroSharp.Confluent", StringComparison.Ordinal),
                Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Activities.Add,
            };
            ActivitySource.AddActivityListener(_tracing);
        }

        public ConcurrentBag<Measurement> Measurements { get; } = [];

        public ConcurrentBag<Activity> Activities { get; } = [];

        public void Dispose()
        {
            _meters.Dispose();
            _tracing.Dispose();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            Measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }
}
