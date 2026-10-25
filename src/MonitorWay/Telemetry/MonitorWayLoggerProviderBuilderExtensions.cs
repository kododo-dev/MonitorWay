using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Telemetry;
using Microsoft.Extensions.DependencyInjection;

// In the namespace of the builder it extends, as the exporters of OpenTelemetry are, so it shows up next to them.
namespace OpenTelemetry.Logs;

/// <summary>
/// Adds MonitorWay to the OpenTelemetry SDK as one more log exporter, next to any others the application uses.
/// </summary>
public static class MonitorWayLoggerProviderBuilderExtensions
{
    /// <summary>
    /// Keeps the application's logs in MonitorWay. Needs <c>builder.AddMonitorWay()</c>, which also turns on the
    /// formatted message and the scopes in the OpenTelemetry logging options.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.AddMonitorWay(x => x.AddUi());
    /// builder.Services.AddOpenTelemetry().WithLogging(l => l.AddMonitorWay());
    /// </code>
    /// </example>
    public static LoggerProviderBuilder AddMonitorWay(this LoggerProviderBuilder builder) =>
        // Exported right away rather than in batches: the buffer is in memory and the live view should not wait.
        builder.AddProcessor(services => new SimpleLogRecordExportProcessor(
            new LogExporter(
                services.GetService<MemoryLogBuffer>() ?? throw new InvalidOperationException(
                    "MonitorWay is not registered: call builder.AddMonitorWay() before adding it to OpenTelemetry."),
                services.GetService<MemoryErrorStore>(),
                services.GetService<ServiceDirectory>())));
}
