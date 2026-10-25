using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// A second service of the demo, the shop's warehouse. It stands for another process, in any language: it has an
/// OpenTelemetry SDK of its own (service <c>warehouse</c>) and sends its traces, logs and metrics with the OTLP exporter
/// to the demo's receiver, <c>/monitor/otlp</c>. The shop's reservations call it, so their traces span both services.
/// </summary>
internal sealed class Warehouse : IDisposable
{
    public const string SourceName = "Kododo.Warehouse";

    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>("warehouse.reservations", "{reservation}", "Reservations by result");
    private static readonly Histogram<double> PickDuration = Meter.CreateHistogram<double>("warehouse.pick.duration", "s", "Time to find the items on the shelves");

    private readonly object _lock = new();
    private TracerProvider? _tracing;
    private MeterProvider? _metrics;
    private ILoggerFactory? _loggers;
    private ILogger? _logger;

    /// <summary>Starts the warehouse's SDK once the demo's address is known: its exporter sends there.</summary>
    public void Start(Uri demo)
    {
        lock (_lock)
        {
            if (_tracing is not null)
                return;
            var resource = ResourceBuilder.CreateDefault().AddService("warehouse", serviceVersion: "2.4.1");
            void Otlp(OtlpExporterOptions o, string signal)
            {
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Endpoint = new Uri(demo, $"monitor/otlp/v1/{signal}");
            }

            _tracing = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(SourceName)
                .AddOtlpExporter(o => Otlp(o, "traces"))
                .Build();
            // As exporters send by default: running totals, every 10 seconds rather than every minute.
            _metrics = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(SourceName)
                .AddView("warehouse.pick.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1] })
                .AddOtlpExporter((o, reader) =>
                {
                    Otlp(o, "metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                })
                .Build();
            _loggers = LoggerFactory.Create(l => l.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(resource);
                o.IncludeFormattedMessage = true;
                o.AddOtlpExporter(e => Otlp(e, "logs"));
            }));
            _logger = _loggers.CreateLogger("Warehouse.Picking.PickService");
        }
    }

    /// <summary>
    /// Reserves items, as a request from the shop would: the shop's call is the remote parent of the warehouse's span.
    /// Returns whether the items were found.
    /// </summary>
    public async Task<bool> ReserveAsync(ActivityContext caller, string product, int quantity, CancellationToken cancellationToken)
    {
        var random = Random.Shared;
        using var request = Source.StartActivity("POST /reservations", ActivityKind.Server, caller);
        request?.SetTag("http.request.method", "POST");
        request?.SetTag("http.route", "/reservations");
        request?.SetTag("product", product);

        var started = Stopwatch.GetTimestamp();
        var result = "reserved";
        try
        {
            using (var pick = Source.StartActivity("Pick"))
            {
                var slow = random.Next(20) == 0;
                pick?.SetTag("warehouse.zone", $"Z{random.Next(1, 6)}");
                await Task.Delay(slow ? random.Next(300, 900) : random.Next(5, 60), cancellationToken);
                if (slow)
                    _logger?.LogWarning("Scanner in zone {Zone} answered slowly while picking {Product}", pick?.GetTagItem("warehouse.zone"), product);
            }

            if (random.Next(40) == 0)
            {
                try
                {
                    throw new IOException($"Shelf label of '{product}' could not be read.");
                }
                catch (IOException e)
                {
                    request?.SetStatus(ActivityStatusCode.Error, e.Message);
                    request?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                    {
                        ["exception.type"] = e.GetType().FullName,
                        ["exception.message"] = e.Message,
                        ["exception.stacktrace"] = e.ToString(),
                    }));
                    _logger?.LogError(e, "Reservation of {Quantity} x {Product} failed", quantity, product);
                    result = "failed";
                    return false;
                }
            }

            _logger?.LogInformation("Reserved {Quantity} x {Product}", quantity, product);
            return true;
        }
        finally
        {
            PickDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            Reservations.Add(1, new KeyValuePair<string, object?>("result", result));
            request?.SetTag("http.response.status_code", result == "reserved" ? 201 : 500);
        }
    }

    public void Dispose()
    {
        _tracing?.Dispose();
        _metrics?.Dispose();
        _loggers?.Dispose();
    }
}
