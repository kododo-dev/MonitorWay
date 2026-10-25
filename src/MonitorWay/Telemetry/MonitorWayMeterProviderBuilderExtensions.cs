using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Telemetry;
using Microsoft.Extensions.DependencyInjection;

// In the namespace of the builder it extends, as the exporters of OpenTelemetry are, so it shows up next to them.
namespace OpenTelemetry.Metrics;

/// <summary>
/// Adds MonitorWay to the OpenTelemetry SDK as one more metric reader, next to any others the application uses.
/// </summary>
public static class MonitorWayMeterProviderBuilderExtensions
{
    /// <summary>
    /// Keeps the application's metrics in MonitorWay, collected every 10 seconds (<c>ConfigureMetrics</c> changes it).
    /// Needs <c>builder.AddMonitorWay()</c>. Only the meters and instrumentations added to the metrics are collected,
    /// e.g. <c>AddAspNetCoreInstrumentation()</c>, <c>AddRuntimeInstrumentation()</c> or <c>AddMeter("MyApp")</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.AddMonitorWay(x => x.AddUi());
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMonitorWay());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddMonitorWay(this MeterProviderBuilder builder) =>
        builder.AddReader(services =>
        {
            var store = services.GetService<MemoryMetricStore>() ?? throw new InvalidOperationException(
                "MonitorWay is not registered: call builder.AddMonitorWay() before adding it to OpenTelemetry.");
            // Deltas: what happened in each period, which is what the store keeps; independent of other readers.
            return new PeriodicExportingMetricReader(
                new MetricExporter(store, services.GetService<IgnoredRequests>(), services.GetService<ServiceDirectory>()), (int)store.ExportInterval.TotalMilliseconds)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Delta,
            };
        });
}
