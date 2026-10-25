using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Limits of the metrics kept in memory. Every series (an instrument with one set of attribute values) is kept in three
/// resolutions: 10 seconds for the last hour, 1 minute for the last day and 10 minutes for the last week.
/// </summary>
public sealed class MetricStoreOptions
{
    /// <summary>Default value of <see cref="MaxTotalBytes"/>: 64 MB.</summary>
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Series kept per instrument. Attributes with many values (a user id, a raw path) would create a series each; above
    /// the limit new series are counted, not kept. Default: 500.
    /// </summary>
    public int MaxSeriesPerInstrument { get; set; } = 500;

    /// <summary>Approximate memory of all series together; above it new series are counted, not kept. Default: 64 MB.</summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;

    /// <summary>
    /// How often the measurements are collected from the OpenTelemetry SDK. Default: 10 seconds, the finest resolution
    /// kept; a longer interval leaves gaps in it.
    /// </summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Reads the options from configuration (<c>MonitorWay:Metrics</c>); sizes accept units, e.g. <c>"64MB"</c>.</summary>
    internal static void Bind(IConfigurationSection section, MetricStoreOptions options)
    {
        if (section["MaxSeriesPerInstrument"] is { } maxSeries)
            options.MaxSeriesPerInstrument = int.Parse(maxSeries, CultureInfo.InvariantCulture);
        if (section["MaxTotalBytes"] is { } maxTotal)
            options.MaxTotalBytes = MemoryBufferConfiguration.ParseSize(maxTotal, section.GetSection("MaxTotalBytes").Path);
        if (section["ExportInterval"] is { } interval)
            options.ExportInterval = TimeSpan.Parse(interval, CultureInfo.InvariantCulture);
    }

    internal void Validate()
    {
        if (MaxSeriesPerInstrument <= 0 || MaxTotalBytes <= 0)
            throw new InvalidOperationException("MonitorWay: MaxSeriesPerInstrument and MaxTotalBytes of the metrics must be positive.");
        if (ExportInterval < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("MonitorWay: the ExportInterval of the metrics must be at least a second.");
    }
}
