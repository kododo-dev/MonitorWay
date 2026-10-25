using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using ProtoMetrics = OpenTelemetry.Proto.Metrics.V1;
using ProtoTrace = OpenTelemetry.Proto.Trace.V1;

namespace Kododo.MonitorWay.Otlp;

/// <summary>
/// Puts what other services send over OTLP into the same stores the application's own telemetry goes to, so their
/// traces, logs, errors and metrics show next to it, told apart by <c>service.name</c>.
/// </summary>
internal sealed class OtlpIngest(
    MemoryTraceStore traces, MemoryLogBuffer logs, MemoryMetricStore metrics, MemoryErrorStore errors, ServiceDirectory services,
    TimeProvider time)
{
    private const string UnknownService = "unknown_service";
    private const string OriginalFormat = "{OriginalFormat}";

    // Span flags (trace.proto): whether the flags say if the parent is remote, and if so whether it is.
    private const uint HasIsRemote = 0x100;
    private const uint IsRemote = 0x200;

    /// <returns>How many spans were kept.</returns>
    public int Traces(ExportTraceServiceRequest request)
    {
        var kept = 0;
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var service = ServiceName(resourceSpans.Resource);
            services.Seen(service, ServiceDirectory.Traces);
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                var scope = NullIfEmpty(scopeSpans.Scope?.Name);
                foreach (var span in scopeSpans.Spans)
                {
                    if (span.TraceId.Length != 16 || span.SpanId.Length != 8)
                        continue;
                    var captured = Span(span, service, scope);
                    traces.Add(captured, Size(captured));
                    kept++;
                }
            }
        }
        return kept;
    }

    /// <returns>How many log records were kept.</returns>
    public int Logs(ExportLogsServiceRequest request)
    {
        var kept = 0;
        foreach (var resourceLogs in request.ResourceLogs)
        {
            var service = ServiceName(resourceLogs.Resource);
            services.Seen(service, ServiceDirectory.Logs);
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                var category = scopeLogs.Scope?.Name ?? "";
                foreach (var record in scopeLogs.LogRecords)
                {
                    if (Log(record, service, category))
                        kept++;
                }
            }
        }
        return kept;
    }

    /// <returns>How many data points were kept.</returns>
    public int Metrics(ExportMetricsServiceRequest request)
    {
        var kept = 0;
        foreach (var resourceMetrics in request.ResourceMetrics)
        {
            var service = ServiceName(resourceMetrics.Resource);
            services.Seen(service, ServiceDirectory.Metrics);
            foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
            {
                var meter = NullIfEmpty(scopeMetrics.Scope?.Name) ?? service;
                foreach (var metric in scopeMetrics.Metrics)
                    kept += Metric(metric, meter, service);
            }
        }
        return kept;
    }

    private static TraceSpan Span(ProtoTrace.Span span, string service, string? scope)
    {
        var kind = span.Kind switch
        {
            ProtoTrace.Span.Types.SpanKind.Server => TraceSpanKind.Server,
            ProtoTrace.Span.Types.SpanKind.Client => TraceSpanKind.Client,
            ProtoTrace.Span.Types.SpanKind.Producer => TraceSpanKind.Producer,
            ProtoTrace.Span.Types.SpanKind.Consumer => TraceSpanKind.Consumer,
            _ => TraceSpanKind.Internal,
        };
        var parent = span.ParentSpanId.Length == 8 ? Hex(span.ParentSpanId) : null;
        // Without the flag (older SDKs), the span a service starts for a request it received is taken as its root.
        var remoteParent = parent is not null && ((span.Flags & HasIsRemote) != 0
            ? (span.Flags & IsRemote) != 0
            : kind is TraceSpanKind.Server or TraceSpanKind.Consumer);

        var start = Time(span.StartTimeUnixNano);
        var end = Time(span.EndTimeUnixNano);
        return new TraceSpan(
            Hex(span.TraceId), Hex(span.SpanId), parent, span.Name, kind, start, end > start ? end - start : TimeSpan.Zero,
            span.Status?.Code switch
            {
                ProtoTrace.Status.Types.StatusCode.Error => TraceSpanStatus.Error,
                ProtoTrace.Status.Types.StatusCode.Ok => TraceSpanStatus.Ok,
                _ => TraceSpanStatus.Unset,
            },
            NullIfEmpty(span.Status?.Message), service, scope, Attributes(span.Attributes),
            span.Events.Select(e => new SpanEvent(e.Name, Time(e.TimeUnixNano), Attributes(e.Attributes))).ToArray(),
            span.Links.Where(l => l.TraceId.Length == 16 && l.SpanId.Length == 8)
                .Select(l => new SpanLink(Hex(l.TraceId), Hex(l.SpanId))).ToArray(),
            remoteParent);
    }

    private bool Log(OpenTelemetry.Proto.Logs.V1.LogRecord record, string service, string category)
    {
        string? template = null, exceptionType = null, exceptionMessage = null, stackTrace = null;
        var properties = new List<KeyValuePair<string, string?>>();
        foreach (var attribute in record.Attributes)
        {
            var value = Text(attribute.Value);
            switch (attribute.Key)
            {
                case OriginalFormat: template = value; break;
                case "exception.type": exceptionType = value; break;
                case "exception.message": exceptionMessage = value; break;
                case "exception.stacktrace": stackTrace = value; break;
                default: properties.Add(new(attribute.Key, value)); break;
            }
        }
        if (!string.IsNullOrEmpty(record.EventName))
            properties.Add(new("EventName", record.EventName));

        var timestamp = record.TimeUnixNano != 0 ? Time(record.TimeUnixNano)
            : record.ObservedTimeUnixNano != 0 ? Time(record.ObservedTimeUnixNano) : time.GetUtcNow();
        var message = Text(record.Body) ?? template ?? "";
        var exception = stackTrace ?? (exceptionType is null ? null : $"{exceptionType}: {exceptionMessage}");
        var traceId = record.TraceId.Length == 16 ? Hex(record.TraceId) : null;
        var spanId = record.SpanId.Length == 8 ? Hex(record.SpanId) : null;

        var stored = logs.Append(EntryCapture.Create(
            timestamp, Level(record), category, message, template == message ? null : template, exception, properties,
            traceId, spanId, exceptionType, service));
        if (stored is null)
            return false;

        if (exception is not null)
        {
            errors.Add(new ReportedException(
                timestamp, exceptionType ?? "Exception", exceptionMessage ?? message, exception, service, null,
                traceId, spanId, stored.Id, category));
        }
        return true;
    }

    private int Metric(ProtoMetrics.Metric metric, string meter, string service)
    {
        var unit = NullIfEmpty(metric.Unit);
        var description = NullIfEmpty(metric.Description);
        var kept = 0;
        switch (metric.DataCase)
        {
            case ProtoMetrics.Metric.DataOneofCase.Sum:
                var sum = metric.Sum;
                var instrument = new MetricInstrument(meter, metric.Name, unit, description, sum.IsMonotonic ? MetricKind.Counter : MetricKind.UpDownCounter);
                var cumulative = sum.AggregationTemporality == ProtoMetrics.AggregationTemporality.Cumulative;
                foreach (var point in sum.DataPoints)
                {
                    metrics.Record(instrument, Series(point.Attributes, service), Time(point.TimeUnixNano), new Measurement(Value(point)), cumulative);
                    kept++;
                }
                break;
            case ProtoMetrics.Metric.DataOneofCase.Gauge:
                var gauge = new MetricInstrument(meter, metric.Name, unit, description, MetricKind.Gauge);
                foreach (var point in metric.Gauge.DataPoints)
                {
                    metrics.Record(gauge, Series(point.Attributes, service), Time(point.TimeUnixNano), new Measurement(Value(point)));
                    kept++;
                }
                break;
            case ProtoMetrics.Metric.DataOneofCase.Histogram:
                var histogram = new MetricInstrument(meter, metric.Name, unit, description, MetricKind.Histogram);
                var cumulativeHistogram = metric.Histogram.AggregationTemporality == ProtoMetrics.AggregationTemporality.Cumulative;
                foreach (var point in metric.Histogram.DataPoints)
                {
                    var bounds = point.ExplicitBounds.ToArray();
                    var counts = point.BucketCounts.Select(c => (long)c).ToArray();
                    var valid = counts.Length == bounds.Length + 1;
                    metrics.Record(histogram, Series(point.Attributes, service), Time(point.TimeUnixNano), new Measurement(
                        point.HasSum ? point.Sum : 0, (long)point.Count, point.HasMin ? point.Min : null, point.HasMax ? point.Max : null,
                        valid ? bounds : null, valid ? counts : null), cumulativeHistogram);
                    kept++;
                }
                break;
            // Exponential histograms and summaries are not kept, as from the application's own SDK.
        }
        return kept;
    }

    private static double Value(ProtoMetrics.NumberDataPoint point) =>
        point.ValueCase == ProtoMetrics.NumberDataPoint.ValueOneofCase.AsInt ? point.AsInt : point.AsDouble;

    /// <summary>The attributes of a data point, sorted, with the service as one of them.</summary>
    private static KeyValuePair<string, string?>[] Series(RepeatedField<KeyValue> attributes, string service)
    {
        var series = Attributes(attributes).ToList();
        if (!series.Exists(a => a.Key == "service.name"))
            series.Add(new("service.name", service));
        series.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return series.ToArray();
    }

    private static LogLevel Level(OpenTelemetry.Proto.Logs.V1.LogRecord record)
    {
        var number = (int)record.SeverityNumber;
        if (number > 0)
        {
            return number switch
            {
                <= 4 => LogLevel.Trace,
                <= 8 => LogLevel.Debug,
                <= 12 => LogLevel.Information,
                <= 16 => LogLevel.Warning,
                <= 20 => LogLevel.Error,
                _ => LogLevel.Critical,
            };
        }
        return record.SeverityText.ToUpperInvariant() switch
        {
            "TRACE" or "VERBOSE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "FATAL" or "CRITICAL" => LogLevel.Critical,
            _ => LogLevel.Information,
        };
    }

    private static string ServiceName(Resource? resource) =>
        resource?.Attributes.FirstOrDefault(a => a.Key == "service.name") is { } name && Text(name.Value) is { Length: > 0 } text
            ? text
            : UnknownService;

    private static KeyValuePair<string, string?>[] Attributes(RepeatedField<KeyValue> attributes)
    {
        var result = new KeyValuePair<string, string?>[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
            result[i] = new(attributes[i].Key, Text(attributes[i].Value));
        return result;
    }

    /// <summary>A value as text, as the application's own attributes are kept.</summary>
    private static string? Text(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.ArrayValue => string.Join(", ", value.ArrayValue.Values.Select(Text)),
        AnyValue.ValueOneofCase.KvlistValue => "{" + string.Join(", ", value.KvlistValue.Values.Select(kv => $"{kv.Key}: {Text(kv.Value)}")) + "}",
        AnyValue.ValueOneofCase.BytesValue => value.BytesValue.ToBase64(),
        _ => null,
    };

    private static DateTimeOffset Time(ulong unixNanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(unixNanos / 100));

    private static string Hex(ByteString bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>Approximate memory of a span, for the limits of the trace store; as for the application's own spans.</summary>
    private static int Size(TraceSpan span)
    {
        const int SpanOverhead = 240;
        const int ItemOverhead = 48;
        long size = SpanOverhead + span.Name.Length * sizeof(char);
        foreach (var (key, value) in span.Attributes)
            size += ItemOverhead + (key.Length + (value?.Length ?? 0)) * sizeof(char);
        foreach (var e in span.Events)
        {
            size += ItemOverhead + e.Name.Length * sizeof(char);
            foreach (var (key, value) in e.Attributes)
                size += ItemOverhead + (key.Length + (value?.Length ?? 0)) * sizeof(char);
        }
        size += span.Links.Count * (ItemOverhead + 48 * sizeof(char));
        return (int)Math.Min(size, int.MaxValue);
    }
}
