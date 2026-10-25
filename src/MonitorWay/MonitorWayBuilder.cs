using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kododo.MonitorWay;

/// <summary>
/// Fluent builder used to configure MonitorWay during application startup.
/// An instance is passed to the <c>configure</c> delegate of
/// <see cref="HostApplicationBuilderExtensions.AddMonitorWay"/>.
/// </summary>
public sealed class MonitorWayBuilder(IHostApplicationBuilder builder)
{
    /// <summary>The configuration section MonitorWay reads its settings from.</summary>
    public const string ConfigurationSection = "MonitorWay";

    /// <summary>
    /// Provides access to the application's <see cref="IServiceCollection"/>
    /// so that extension packages (e.g. <c>MonitorWay.UI</c>) can register their own services.
    /// </summary>
    public IServiceCollection Services => builder.Services;

    /// <summary>The application host builder, for extension packages that need its configuration or environment.</summary>
    public IHostApplicationBuilder Host => builder;

    internal MemoryBufferOptions? MemoryBuffer { get; private set; }

    internal List<FileSourceOptions> Files { get; } = [];

    /// <summary>
    /// Reads log files matching a glob pattern. Files appearing later (e.g. after rotation) are picked up
    /// automatically, and files that grow are followed.
    /// </summary>
    /// <param name="pattern">
    /// Glob pattern such as <c>logs/*.clef</c> or <c>/var/log/app/**/*.log</c>; a relative pattern is
    /// resolved against the content root.
    /// </param>
    /// <param name="configure">
    /// Optional delegate for the format. By default it is detected from each file: CLEF, JSON lines, the default
    /// text layouts of Serilog, NLog and log4net, or plain text. For a custom text layout pass the output template:
    /// <c>f => f.UseTemplate("{Timestamp:HH:mm:ss} {Level:u3} {SourceContext}: {Message:lj}{NewLine}{Exception}")</c>.
    /// </param>
    public MonitorWayBuilder AddFiles(string pattern, Action<FileSourceOptions>? configure = null)
    {
        var options = new FileSourceOptions { Pattern = pattern };
        configure?.Invoke(options);
        Files.Add(options);
        return this;
    }

    /// <summary>
    /// Sets how long the logs are kept in memory, per level. Settings are read from the <c>MonitorWay:Memory</c>
    /// configuration section first, then <paramref name="configure"/> is applied on top of them. Without this call the
    /// defaults apply: the errors are kept longer than the rest, within 64 MB in total.
    /// </summary>
    /// <example>
    /// <code>
    /// x.AddMemoryBuffer(m =>
    /// {
    ///     m.MaxTotalBytes = 128 * 1024 * 1024;
    ///     m.Pool(LogLevel.Error, LogLevel.Critical).KeepEntries(5_000).KeepFor(TimeSpan.FromDays(7));
    ///     m.Pool(LogLevel.Warning).KeepFor(TimeSpan.FromHours(24));
    /// });
    /// </code>
    /// </example>
    public MonitorWayBuilder AddMemoryBuffer(Action<MemoryBufferOptions>? configure = null)
    {
        if (MemoryBuffer is null)
        {
            MemoryBuffer = new MemoryBufferOptions();
            MemoryBufferConfiguration.Bind(builder.Configuration.GetSection($"{ConfigurationSection}:Memory"), MemoryBuffer);
        }
        configure?.Invoke(MemoryBuffer);
        return this;
    }

    internal TraceStoreOptions? Traces { get; private set; }

    /// <summary>
    /// Sets how long the traces are kept in memory. Settings are read from the <c>MonitorWay:Traces</c> configuration
    /// section first, then <paramref name="configure"/> is applied on top of them. Without this call the defaults
    /// apply: failed and slow traces are kept apart from the rest, within 64 MB in total.
    /// </summary>
    /// <example>
    /// <code>
    /// x.ConfigureTraces(t =>
    /// {
    ///     t.SlowThreshold = TimeSpan.FromMilliseconds(500);
    ///     t.Errors.KeepTraces(5_000).KeepFor(TimeSpan.FromDays(1));
    ///     t.Other.KeepFor(TimeSpan.FromHours(1));
    /// });
    /// </code>
    /// </example>
    public MonitorWayBuilder ConfigureTraces(Action<TraceStoreOptions>? configure = null)
    {
        if (Traces is null)
        {
            Traces = new TraceStoreOptions();
            TraceStoreOptions.Bind(builder.Configuration.GetSection($"{ConfigurationSection}:Traces"), Traces);
        }
        configure?.Invoke(Traces);
        return this;
    }

    internal MetricStoreOptions? Metrics { get; private set; }

    /// <summary>
    /// Sets the limits of the metrics kept in memory and how often they are collected. Settings are read from the
    /// <c>MonitorWay:Metrics</c> configuration section first, then <paramref name="configure"/> is applied on top of them.
    /// Without this call the defaults apply: every 10 seconds, up to 500 series per instrument, within 64 MB.
    /// </summary>
    public MonitorWayBuilder ConfigureMetrics(Action<MetricStoreOptions>? configure = null)
    {
        if (Metrics is null)
        {
            Metrics = new MetricStoreOptions();
            MetricStoreOptions.Bind(builder.Configuration.GetSection($"{ConfigurationSection}:Metrics"), Metrics);
        }
        configure?.Invoke(Metrics);
        return this;
    }

    internal MetricStoreOptions MetricsOrDefault()
    {
        if (Metrics is null)
            ConfigureMetrics();
        return Metrics!;
    }

    internal ErrorStoreOptions? Errors { get; private set; }

    /// <summary>
    /// Sets how many error groups are kept and for how long. Settings are read from the <c>MonitorWay:Errors</c>
    /// configuration section first, then <paramref name="configure"/> is applied on top of them. Without this call the
    /// defaults apply: 1 000 groups with their 50 latest occurrences, for 7 days since a group was last seen.
    /// </summary>
    public MonitorWayBuilder ConfigureErrors(Action<ErrorStoreOptions>? configure = null)
    {
        if (Errors is null)
        {
            Errors = new ErrorStoreOptions();
            ErrorStoreOptions.Bind(builder.Configuration.GetSection($"{ConfigurationSection}:Errors"), Errors);
        }
        configure?.Invoke(Errors);
        return this;
    }

    internal ErrorStoreOptions ErrorsOrDefault()
    {
        if (Errors is null)
            ConfigureErrors();
        return Errors!;
    }

    internal TraceStoreOptions TracesOrDefault()
    {
        if (Traces is null)
            ConfigureTraces();
        return Traces!;
    }

    internal MemoryBufferOptions MemoryBufferOrDefault()
    {
        if (MemoryBuffer is null)
            AddMemoryBuffer();
        return MemoryBuffer!;
    }
}
