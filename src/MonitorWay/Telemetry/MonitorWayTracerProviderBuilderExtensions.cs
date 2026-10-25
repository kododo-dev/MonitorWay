using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Telemetry;
using Microsoft.Extensions.DependencyInjection;

// In the namespace of the builder it extends, as the exporters of OpenTelemetry are, so it shows up next to them.
namespace OpenTelemetry.Trace;

/// <summary>
/// Adds MonitorWay to the OpenTelemetry SDK as one more trace exporter, next to any others the application uses.
/// </summary>
public static class MonitorWayTracerProviderBuilderExtensions
{
    /// <summary>
    /// Keeps the application's traces in MonitorWay. Needs <c>builder.AddMonitorWay()</c>. Only the spans of the
    /// sources and instrumentations added to the tracing are recorded, e.g. <c>AddAspNetCoreInstrumentation()</c> or
    /// <c>AddSource("MyApp")</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.AddMonitorWay(x => x.AddUi());
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMonitorWay());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddMonitorWay(this TracerProviderBuilder builder) =>
        // Exported as each span ends rather than in batches: the store is in memory and only gathers the spans.
        builder.AddProcessor(services => new SimpleActivityExportProcessor(
            new TraceExporter(
                services.GetService<MemoryTraceStore>() ?? throw new InvalidOperationException(
                    "MonitorWay is not registered: call builder.AddMonitorWay() before adding it to OpenTelemetry."),
                services.GetService<ServiceDirectory>())));
}
