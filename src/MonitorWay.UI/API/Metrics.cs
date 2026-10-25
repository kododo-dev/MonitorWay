using System.ComponentModel;
using System.Globalization;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

internal sealed record AttributeKey(string Key, int Values);

internal sealed record InstrumentRow(
    string Key, string Meter, string Name, string? Unit, string? Description, string Kind, int Series, long DroppedSeries,
    DateTimeOffset LastUpdate, AttributeKey[] Attributes, string[] Stats);

[Description("Lists the metric instruments the application reports (counters, up-down counters, gauges, histograms) with their " +
             "unit, attribute keys and the statistics their series offer. Start here to get an instrument key.")]
internal sealed record GetMetrics : IRequest<InstrumentRow[]>;

internal sealed class GetMetricsHandler(MemoryMetricStore store) : IRequestHandler<GetMetrics, InstrumentRow[]>
{
    public Task<InstrumentRow[]> HandleAsync(GetMetrics request, CancellationToken cancellationToken) =>
        Task.FromResult(store.List().Select(i => new InstrumentRow(
            i.Instrument.Key, i.Instrument.Meter, i.Instrument.Name, i.Instrument.Unit, i.Instrument.Description,
            i.Instrument.Kind.ToString(), i.Series, i.DroppedSeries, i.LastUpdate,
            i.AttributeKeys.Select(k => new AttributeKey(k.Key, k.Value)).ToArray(),
            MetricStats.For(i.Instrument.Kind).ToArray())).ToArray());
}

internal sealed record AttributeFilter(string Key, string? Value);

/// <param name="Values">One per period; <c>null</c> where there is no data.</param>
/// <param name="Total">The statistic over the whole range.</param>
internal sealed record SeriesDto(string Label, Property[] Attributes, double?[] Values, double? Total, int Series);

/// <param name="Times">Start of each period, in Unix seconds.</param>
internal sealed record MetricSeriesPage(
    string Key, string Name, string? Unit, string Kind, string Stat, int ResolutionSeconds, long[] Times, SeriesDto[] Series,
    int HiddenGroups, string? Error = null);

[Description("Reads the time series of an instrument within a range: one statistic per period, for all its series together or " +
             "grouped by attributes, plus the statistic over the whole range (total). Values are in the instrument's unit " +
             "(e.g. seconds for durations, bytes for By); a rate is per second. Periods are 10 s up to an hour back, 1 min up to a " +
             "day, 10 min up to a week, so an hour is 360 values per series: for a single number read the total.")]
internal sealed record GetMetricSeries(
    [property: Description("Key of the instrument, as in the list of metrics (meter/name).")] string Key,
    [property: Description("Start of the range (ISO 8601); by default an hour ago.")] DateTimeOffset? From = null,
    [property: Description("End of the range (ISO 8601); by default now.")] DateTimeOffset? To = null,
    [property: Description("Counters: sum or rate (per second). Gauges: avg, last, min, max. Histograms: p50, p90, p95, p99, avg, max, " +
                           "count, rate, or percentiles for p50, p95 and p99 together. By default the first of these.")] string? Stat = null,
    [property: Description("Attribute keys to group the series by, e.g. http.route; all series together when left out.")] string[]? GroupBy = null,
    [property: Description("Only series with these attribute values.")] AttributeFilter[]? Filter = null,
    [property: Description("How many groups to return, the largest first; at most 50.")] int MaxGroups = 10) : IRequest<MetricSeriesPage?>;

internal sealed class GetMetricSeriesHandler(MemoryMetricStore store) : IRequestHandler<GetMetricSeries, MetricSeriesPage?>
{
    private static readonly string[] Percentiles = ["p50", "p95", "p99"];

    public Task<MetricSeriesPage?> HandleAsync(GetMetricSeries request, CancellationToken cancellationToken)
    {
        if (store.FindInstrument(request.Key) is not { } instrument)
            return Task.FromResult<MetricSeriesPage?>(null);

        var stats = MetricStats.For(instrument.Kind);
        var stat = request.Stat ?? stats[0];
        var percentiles = stat == "percentiles" && instrument.Kind == MetricKind.Histogram;
        if (!percentiles && !stats.Contains(stat))
            return Task.FromResult<MetricSeriesPage?>(Empty(instrument, stat, $"'{stat}' does not apply to a {instrument.Kind}: use {string.Join(", ", stats)}."));

        var to = request.To ?? store.Now;
        var from = request.From ?? to.AddHours(-1);
        var groupBy = percentiles ? [] : request.GroupBy ?? [];
        var filter = request.Filter is { Length: > 0 } f
            ? attributes => f.All(c => attributes.Any(a => a.Key == c.Key && a.Value == c.Value))
            : (Func<KeyValuePair<string, string?>[], bool>?)null;
        var timeline = store.Aggregate(instrument.Key, from, to, groupBy, filter, Math.Clamp(request.MaxGroups, 1, 50));
        if (timeline is null)
            return Task.FromResult<MetricSeriesPage?>(null);

        var seconds = timeline.Resolution.Seconds;
        var times = Enumerable.Range(0, timeline.Length).Select(i => timeline.Start.ToUnixTimeSeconds() + (long)i * seconds).ToArray();
        SeriesDto Series(MetricGroup group, string statistic, string? label = null) => new(
            label ?? Label(group.Attributes),
            group.Attributes.Select(a => new Property(a.Key, a.Value)).ToArray(),
            group.Buckets.Select(b => MetricStats.Value(instrument.Kind, statistic, b, seconds, timeline.Bounds)).ToArray(),
            MetricStats.Value(instrument.Kind, statistic, group.Total, Math.Max(1, (to - from).TotalSeconds), timeline.Bounds),
            group.Series);

        var series = percentiles
            ? timeline.Groups.SelectMany(g => Percentiles.Select(p => Series(g, p, p))).ToArray()
            : timeline.Groups.Select(g => Series(g, stat)).ToArray();
        return Task.FromResult<MetricSeriesPage?>(new MetricSeriesPage(
            instrument.Key, instrument.Name, instrument.Unit, instrument.Kind.ToString(), stat, seconds, times, series,
            timeline.HiddenGroups));
    }

    private static MetricSeriesPage Empty(MetricInstrument instrument, string stat, string error) =>
        new(instrument.Key, instrument.Name, instrument.Unit, instrument.Kind.ToString(), stat, 0, [], [], 0, error);

    private static string Label(KeyValuePair<string, string?>[] attributes) =>
        attributes.Length == 0 ? "all" : string.Join(" ", attributes.Select(a => a.Value ?? "(none)"));
}

internal sealed record RouteStats(
    string? Service, string? Method, string Route, long Requests, long Errors, double? P50, double? P95, double? P99);

/// <param name="Available">Whether the application reports <c>http.server.request.duration</c>, which the overview is made of.</param>
/// <param name="Times">Start of each period, in Unix seconds.</param>
/// <param name="P95">Durations in seconds.</param>
internal sealed record ServiceOverview(
    bool Exported, bool Available, int ResolutionSeconds, long[] Times, double?[] Requests, double?[] Errors,
    double?[] P50, double?[] P95, double?[] P99, long TotalRequests, long TotalErrors, double? TotalP50, double? TotalP95,
    double? TotalP99, RouteStats[] Routes);

[Description("Start here to see how the application is doing: the health of its HTTP endpoints within a range (by default the " +
             "last hour), from the http.server.request.duration metric. Totals and, per route, requests, server errors (5xx) and the " +
             "p50, p95 and p99 durations in seconds; also the same over time. With several services (see the list of services) " +
             "each route names its service. Then read the errors, or the traces of a route (http.route:<route>).")]
internal sealed record GetServiceOverview(
    [property: Description("Start of the range (ISO 8601); by default an hour ago.")] DateTimeOffset? From = null,
    [property: Description("End of the range (ISO 8601); by default now.")] DateTimeOffset? To = null,
    [property: Description("Only this service (service.name, as in the list of services); all services when left out.")] string? Service = null)
    : IRequest<ServiceOverview>;

internal sealed class GetServiceOverviewHandler(MemoryMetricStore store) : IRequestHandler<GetServiceOverview, ServiceOverview>
{
    private const string RequestDuration = "http.server.request.duration";
    private static readonly string[] ByRoute = ["service.name", "http.request.method", "http.route"];

    public Task<ServiceOverview> HandleAsync(GetServiceOverview request, CancellationToken cancellationToken)
    {
        var to = request.To ?? store.Now;
        var from = request.From ?? to.AddHours(-1);
        var instrument = store.FindByName(RequestDuration)
            .Where(i => i.Kind == MetricKind.Histogram)
            .OrderBy(i => i.Meter == "Microsoft.AspNetCore.Hosting" ? 0 : 1).FirstOrDefault();
        Func<KeyValuePair<string, string?>[], bool> ofService = request.Service is { Length: > 0 } service
            ? attributes => attributes.Any(a => a.Key == "service.name" && a.Value == service)
            : _ => true;
        var all = instrument is null ? null : store.Aggregate(instrument.Key, from, to, [], ofService);
        if (instrument is null || all is null)
            return Task.FromResult(new ServiceOverview(store.IsExportedTo, false, 0, [], [], [], [], [], [], 0, 0, null, null, null, []));

        bool Failed(KeyValuePair<string, string?>[] attributes) => ofService(attributes) && IsError(attributes);
        var errors = store.Aggregate(instrument.Key, from, to, [], Failed)!;
        var routes = store.Aggregate(instrument.Key, from, to, ByRoute, ofService, maxGroups: 100)!;
        var routeErrors = store.Aggregate(instrument.Key, from, to, ByRoute, Failed, maxGroups: 100)!
            .Groups.ToDictionary(g => g.Key, g => g.Total.Count);

        var seconds = all.Resolution.Seconds;
        var total = all.Groups.FirstOrDefault();
        var errorTotal = errors.Groups.FirstOrDefault();
        double? Stat(MetricBucket? bucket, string stat) => MetricStats.Value(MetricKind.Histogram, stat, bucket, seconds, all.Bounds);
        double?[] Line(MetricGroup? group, string stat) =>
            Enumerable.Range(0, all.Length).Select(i => group is null ? (stat == "count" ? 0 : null) : Stat(group.Buckets[i], stat) ?? (stat == "count" ? 0 : null)).ToArray();

        return Task.FromResult(new ServiceOverview(
            store.IsExportedTo, true, seconds,
            Enumerable.Range(0, all.Length).Select(i => all.Start.ToUnixTimeSeconds() + (long)i * seconds).ToArray(),
            Line(total, "count"), Line(errorTotal, "count"), Line(total, "p50"), Line(total, "p95"), Line(total, "p99"),
            total?.Total.Count ?? 0, errorTotal?.Total.Count ?? 0,
            Stat(total?.Total, "p50"), Stat(total?.Total, "p95"), Stat(total?.Total, "p99"),
            routes.Groups.Select(g => new RouteStats(
                g.Attributes[0].Value, g.Attributes[1].Value, g.Attributes[2].Value ?? "(no route)", g.Total.Count, routeErrors.GetValueOrDefault(g.Key),
                Stat(g.Total, "p50"), Stat(g.Total, "p95"), Stat(g.Total, "p99"))).ToArray()));
    }

    /// <summary>A server error: a 5xx status, or a request that ended with an exception.</summary>
    private static bool IsError(KeyValuePair<string, string?>[] attributes)
    {
        foreach (var (key, value) in attributes)
        {
            if (key == "error.type" && !string.IsNullOrEmpty(value) && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return true;
            if (key is "http.response.status_code" or "http.status_code" &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) && status >= 500)
                return true;
        }
        return false;
    }
}

internal sealed record MetricStatsDto(
    bool Exported, int Instruments, int Series, long DroppedSeries, long Bytes, long MaxTotalBytes, double ExportIntervalSeconds);

[Description("Tells whether metrics are collected, how many instruments and series are kept and how many series were dropped over the limits.")]
internal sealed record GetMetricStats : IRequest<MetricStatsDto>;

internal sealed class GetMetricStatsHandler(MemoryMetricStore store) : IRequestHandler<GetMetricStats, MetricStatsDto>
{
    public Task<MetricStatsDto> HandleAsync(GetMetricStats request, CancellationToken cancellationToken)
    {
        var (instruments, series, dropped, bytes) = store.GetStats();
        return Task.FromResult(new MetricStatsDto(
            store.IsExportedTo, instruments, series, dropped, bytes, store.MaxTotalBytes, store.ExportInterval.TotalSeconds));
    }
}
