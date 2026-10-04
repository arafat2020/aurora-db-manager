using System.Diagnostics.Metrics;
using AuroraDbManager.Api.Application.Monitoring;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Collects what one host's Aurora meter measures. Instruments are matched by their meter's name
/// and by the factory that created the meter, so a recorder never sees another host's metrics,
/// and tests can assert on exact values while running in parallel.
/// </summary>
public sealed class MetricsRecorder : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Measurement> _measurements = [];
    private readonly MeterListener _listener = new();

    /// <param name="Instrument">The instrument's name.</param>
    /// <param name="Value">The measured value.</param>
    /// <param name="Tags">The measurement's tags.</param>
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? Tag(string name) => Tags.GetValueOrDefault(name)?.ToString();
    }

    public MetricsRecorder(IMeterFactory meterFactory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AuroraMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> All
    {
        get
        {
            lock (_lock)
            {
                return _measurements.ToList();
            }
        }
    }

    public IReadOnlyList<Measurement> Of(string instrument) => All.Where(measurement => measurement.Instrument == instrument).ToList();

    /// <summary>The sum of everything measured on a counter, optionally only where a tag has a value.</summary>
    public double Total(string instrument, string? tag = null, string? value = null) =>
        Of(instrument).Where(measurement => tag is null || measurement.Tag(tag) == value).Sum(measurement => measurement.Value);

    /// <summary>Reads the observable instruments now and returns the latest value of one of them.</summary>
    public double Observe(string instrument)
    {
        lock (_lock)
        {
            _measurements.RemoveAll(measurement => measurement.Instrument == instrument);
        }

        _listener.RecordObservableInstruments();
        return Of(instrument).Single().Value;
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_lock)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }
}
