using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Retention settings of the traces kept in memory.
/// </summary>
/// <remarks>
/// Whole traces are kept, not single spans, and the decision is made once a trace is complete: traces with a failed
/// span go to <see cref="Errors"/>, those that took at least <see cref="SlowThreshold"/> to <see cref="Slow"/>, the
/// rest to <see cref="Other"/>. Each pool has its own limits, so a flood of quick requests cannot push out the traces
/// worth looking at. <see cref="MaxTotalBytes"/> applies on top of them, removing from <see cref="Other"/> first and
/// from <see cref="Errors"/> last.
/// </remarks>
public sealed class TraceStoreOptions
{
    /// <summary>Default value of <see cref="MaxTotalBytes"/>: 64 MB.</summary>
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>Hard limit of the approximate memory used by the traces of all pools together.</summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;

    /// <summary>A trace that took at least this long goes to <see cref="Slow"/>. Default: 1 second.</summary>
    public TimeSpan SlowThreshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait for late spans after the root of a trace ended, e.g. of work the request started in the
    /// background. Default: 5 seconds. Spans that come later are added to the trace, which is then stored again.
    /// </summary>
    public TimeSpan CompletionDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Traces with a failed span. Default: the last 2 000.</summary>
    public TracePoolOptions Errors { get; } = new TracePoolOptions().KeepTraces(2_000);

    /// <summary>Traces that took at least <see cref="SlowThreshold"/>. Default: the last 2 000.</summary>
    public TracePoolOptions Slow { get; } = new TracePoolOptions().KeepTraces(2_000);

    /// <summary>All other traces. Default: the last 10 000.</summary>
    public TracePoolOptions Other { get; } = new TracePoolOptions().KeepTraces(10_000);

    /// <summary>Reads the options from configuration (<c>MonitorWay:Traces</c>); sizes accept units, e.g. <c>"64MB"</c>.</summary>
    internal static void Bind(IConfigurationSection section, TraceStoreOptions options)
    {
        if (section["MaxTotalBytes"] is { } maxTotal)
            options.MaxTotalBytes = MemoryBufferConfiguration.ParseSize(maxTotal, section.GetSection("MaxTotalBytes").Path);
        if (section["SlowThreshold"] is { } slow)
            options.SlowThreshold = TimeSpan.Parse(slow, CultureInfo.InvariantCulture);
        if (section["CompletionDelay"] is { } delay)
            options.CompletionDelay = TimeSpan.Parse(delay, CultureInfo.InvariantCulture);

        BindPool(section.GetSection("Errors"), options.Errors);
        BindPool(section.GetSection("Slow"), options.Slow);
        BindPool(section.GetSection("Other"), options.Other);
    }

    private static void BindPool(IConfigurationSection section, TracePoolOptions pool)
    {
        if (section["MaxTraces"] is { } maxTraces)
            pool.MaxTraces = int.Parse(maxTraces, CultureInfo.InvariantCulture);
        if (section["MaxBytes"] is { } maxBytes)
            pool.MaxBytes = MemoryBufferConfiguration.ParseSize(maxBytes, section.GetSection("MaxBytes").Path);
        if (section["MaxAge"] is { } maxAge)
            pool.MaxAge = TimeSpan.Parse(maxAge, CultureInfo.InvariantCulture);
    }

    internal void Validate()
    {
        if (MaxTotalBytes <= 0)
            throw new InvalidOperationException("MonitorWay: the traces' MaxTotalBytes must be positive.");
        if (SlowThreshold < TimeSpan.Zero || CompletionDelay < TimeSpan.Zero)
            throw new InvalidOperationException("MonitorWay: SlowThreshold and CompletionDelay of the traces cannot be negative.");
        foreach (var pool in new[] { Errors, Slow, Other })
        {
            if (pool.MaxTraces < 0 || pool.MaxBytes < 0 || pool.MaxAge < TimeSpan.Zero)
                throw new InvalidOperationException("MonitorWay: the limits of a trace pool cannot be negative.");
        }
    }
}

/// <summary>
/// Limits of one trace pool. A trace is removed as soon as any of the limits is exceeded; a pool without limits is
/// bounded only by <see cref="TraceStoreOptions.MaxTotalBytes"/>.
/// </summary>
public sealed class TracePoolOptions
{
    /// <summary>Maximum number of traces kept in the pool.</summary>
    public int? MaxTraces { get; set; }

    /// <summary>Maximum approximate memory used by the pool, in bytes.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Maximum age of the traces kept in the pool, counted from when they were completed.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Sets <see cref="MaxTraces"/>.</summary>
    public TracePoolOptions KeepTraces(int maxTraces)
    {
        MaxTraces = maxTraces;
        return this;
    }

    /// <summary>Sets <see cref="MaxBytes"/>.</summary>
    public TracePoolOptions KeepBytes(long maxBytes)
    {
        MaxBytes = maxBytes;
        return this;
    }

    /// <summary>Sets <see cref="MaxAge"/>.</summary>
    public TracePoolOptions KeepFor(TimeSpan maxAge)
    {
        MaxAge = maxAge;
        return this;
    }
}
