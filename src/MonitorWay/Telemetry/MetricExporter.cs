using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Kododo.MonitorWay.Telemetry;

/// <summary>
/// Keeps the metrics the OpenTelemetry SDK collects in the metric store. The reader asks for deltas, so a counter or a
/// histogram reports what happened since the previous collection; up-down counters stay cumulative in the SDK, which is
/// their value.
/// </summary>
internal sealed class MetricExporter : BaseExporter<Metric>
{
    public const string ServiceNameAttribute = "service.name";

    private readonly MemoryMetricStore _store;
    private readonly IgnoredRequests? _ignored;

    private readonly ServiceDirectory? _services;
    private string? _serviceName;
    private bool _serviceNameRead;

    public MetricExporter(MemoryMetricStore store, IgnoredRequests? ignored = null, ServiceDirectory? services = null)
    {
        _store = store;
        _ignored = ignored;
        _services = services;
        store.IsExportedTo = true;
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        using var scope = SuppressInstrumentationScope.Begin();
        var serviceName = ServiceName();
        _services?.Seen(serviceName, ServiceDirectory.Metrics);
        foreach (var metric in batch)
        {
            try
            {
                Export(metric, serviceName);
            }
            catch
            {
                // One metric that cannot be read must not cost the others, nor reach the application.
            }
        }
        return ExportResult.Success;
    }

    private void Export(Metric metric, string? serviceName)
    {
        var kind = metric.MetricType switch
        {
            MetricType.LongSum or MetricType.DoubleSum => MetricKind.Counter,
            MetricType.LongSumNonMonotonic or MetricType.DoubleSumNonMonotonic => MetricKind.UpDownCounter,
            MetricType.LongGauge or MetricType.DoubleGauge => MetricKind.Gauge,
            MetricType.Histogram => MetricKind.Histogram,
            _ => (MetricKind?)null, // Exponential histograms are not kept.
        };
        if (kind is null)
            return;

        var instrument = new MetricInstrument(
            metric.MeterName, metric.Name, string.IsNullOrEmpty(metric.Unit) ? null : metric.Unit,
            string.IsNullOrEmpty(metric.Description) ? null : metric.Description, kind.Value);
        var cumulative = metric.Temporality == AggregationTemporality.Cumulative;

        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var attributes = new List<KeyValuePair<string, string?>>();
            var ignored = false;
            foreach (var tag in point.Tags)
            {
                var value = EntryCapture.Format(tag.Value);
                // The requests to the UI, as their logs and traces.
                if (tag.Key == "http.route" && _ignored?.MatchesPath(value) == true)
                    ignored = true;
                attributes.Add(new(tag.Key, value));
            }
            if (ignored)
                continue;
            // As in the series other services send over OTLP: the service is an attribute to group or filter by.
            if (serviceName is not null)
                attributes.Add(new(ServiceNameAttribute, serviceName));
            attributes.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            var measurement = kind switch
            {
                MetricKind.Histogram => Histogram(point),
                MetricKind.Gauge => new Measurement(metric.MetricType == MetricType.LongGauge ? point.GetGaugeLastValueLong() : point.GetGaugeLastValueDouble()),
                _ => new Measurement(metric.MetricType is MetricType.LongSum or MetricType.LongSumNonMonotonic ? point.GetSumLong() : point.GetSumDouble()),
            };
            _store.Record(instrument, attributes.ToArray(), point.EndTime, measurement, cumulative);
        }
    }

    /// <summary><c>service.name</c> of the provider's resource, read once it is known.</summary>
    private string? ServiceName()
    {
        if (_serviceNameRead)
            return _serviceName;
        if (ParentProvider is null)
            return null;
        _serviceName = ParentProvider.GetResource().Attributes.FirstOrDefault(a => a.Key == ServiceNameAttribute).Value as string;
        _serviceNameRead = true;
        return _serviceName;
    }

    private static Measurement Histogram(in MetricPoint point)
    {
        var bounds = new List<double>();
        var counts = new List<long>();
        foreach (var bucket in point.GetHistogramBuckets())
        {
            if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                bounds.Add(bucket.ExplicitBound);
            counts.Add(bucket.BucketCount);
        }
        double? min = null, max = null;
        if (point.TryGetHistogramMinMaxValues(out var low, out var high))
            (min, max) = (low, high);
        return new Measurement(
            point.GetHistogramSum(), point.GetHistogramCount(), min, max,
            counts.Count == bounds.Count + 1 ? bounds.ToArray() : null,
            counts.Count == bounds.Count + 1 ? counts.ToArray() : null);
    }
}
