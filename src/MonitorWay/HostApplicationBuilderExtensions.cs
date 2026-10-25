using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;

namespace Kododo.MonitorWay;

/// <summary>
/// Extension methods for registering MonitorWay with the application host.
/// </summary>
public static class HostApplicationBuilderExtensions
{
    /// <summary>
    /// Registers MonitorWay with the application.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">
    /// Optional delegate for configuring MonitorWay: the retention of the logs, traces, metrics and errors it keeps, log files to read, and
    /// extension packages such as <c>MonitorWay.UI</c>.
    /// </param>
    /// <remarks>
    /// MonitorWay gets the telemetry from the OpenTelemetry SDK, as one more exporter: add it there with
    /// <c>AddMonitorWay()</c>. It turns on the formatted message and the scopes of the OpenTelemetry logging options,
    /// which its log view needs.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddMonitorWay(x => x.AddUi());
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(t => t.AddAspNetCoreInstrumentation().AddMonitorWay())
    ///     .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay())
    ///     .WithLogging(l => l.AddMonitorWay());
    /// </code>
    /// </example>
    public static void AddMonitorWay(this IHostApplicationBuilder builder, Action<MonitorWayBuilder>? configure = null)
    {
        var logWayBuilder = new MonitorWayBuilder(builder);
        configure?.Invoke(logWayBuilder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<LogSources>();
        builder.Services.TryAddSingleton<IgnoredRequests>();
        builder.Services.TryAddSingleton(sp => new ServiceDirectory(sp.GetRequiredService<TimeProvider>()));

        // The logs exported by OpenTelemetry are kept in memory; the retention comes from AddMemoryBuffer or configuration.
        var memoryOptions = logWayBuilder.MemoryBufferOrDefault();
        MemoryLogBuffer.Validate(memoryOptions);
        builder.Services.AddSingleton(sp => new MemoryLogBuffer(
            memoryOptions, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IgnoredRequests>()));

        // So are the traces, whole, once complete; the retention comes from ConfigureTraces or configuration.
        var traceOptions = logWayBuilder.TracesOrDefault();
        traceOptions.Validate();
        builder.Services.AddSingleton(sp => new MemoryTraceStore(
            traceOptions, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IgnoredRequests>(),
            sp.GetRequiredService<MemoryErrorStore>()));

        // The metrics, as time series in three resolutions.
        var metricOptions = logWayBuilder.MetricsOrDefault();
        metricOptions.Validate();
        builder.Services.AddSingleton(sp => new MemoryMetricStore(metricOptions, sp.GetRequiredService<TimeProvider>()));

        // The exceptions of the logs and the spans, grouped.
        var errorOptions = logWayBuilder.ErrorsOrDefault();
        errorOptions.Validate();
        builder.Services.AddSingleton(sp => new MemoryErrorStore(errorOptions, sp.GetRequiredService<TimeProvider>()));
        builder.Services.Configure<OpenTelemetryLoggerOptions>(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        if (logWayBuilder.Files.Count > 0)
        {
            var patterns = logWayBuilder.Files.ToArray();
            foreach (var options in patterns)
            {
                if (options.Format == LogFileFormat.Template)
                {
                    if (string.IsNullOrWhiteSpace(options.Template))
                        throw new InvalidOperationException($"MonitorWay: files '{options.Pattern}' use a template format but have no template.");
                    // Throws on a template that cannot be compiled, at startup rather than on first read.
                    _ = new Files.Formats.TemplateFormat(options.Template);
                }
            }
            builder.Services.AddSingleton(sp => new FileSourceRegistry(
                patterns, builder.Environment.ContentRootPath, sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<FileSourceRegistry>());
        }
    }
}
