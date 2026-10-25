using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class MetricStoreTests
{
    private static readonly MetricInstrument Requests = new("shop", "requests", "{request}", null, MetricKind.Counter);
    private static readonly MetricInstrument Memory = new("shop", "memory", "By", null, MetricKind.Gauge);
    private static readonly MetricInstrument Duration = new("shop", "duration", "s", null, MetricKind.Histogram);
    private static readonly double[] Bounds = [0.1, 0.5, 1];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Counter_KeepsTheIncreasePerPeriod_InEachResolution()
    {
        using var store = Create();
        for (var i = 0; i < 12; i++)
        {
            Record(store, Requests, 5, ("route", "/a"));
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        var fine = store.Aggregate(Requests.Key, Now.AddMinutes(-2), Now, [])!;
        Assert.Equal(10, fine.Resolution.Seconds);
        var group = Assert.Single(fine.Groups);
        Assert.Equal(60, group.Total.Sum);
        Assert.Equal(5, MetricStats.Value(MetricKind.Counter, "sum", group.Buckets[^2], 10, null));
        Assert.Equal(0.5, MetricStats.Value(MetricKind.Counter, "rate", group.Buckets[^2], 10, null));

        var minutes = store.Aggregate(Requests.Key, Now.AddHours(-3), Now, [])!;
        Assert.Equal(60, minutes.Resolution.Seconds);
        Assert.Equal(60, Assert.Single(minutes.Groups).Total.Sum);

        var week = store.Aggregate(Requests.Key, Now.AddDays(-3), Now, [])!;
        Assert.Equal(600, week.Resolution.Seconds);
        Assert.Equal(60, Assert.Single(week.Groups).Total.Sum);
    }

    [Fact]
    public void Series_AreGroupedByAttributes_LargestFirst()
    {
        using var store = Create();
        Record(store, Requests, 1, ("route", "/a"), ("status", "200"));
        Record(store, Requests, 2, ("route", "/a"), ("status", "500"));
        Record(store, Requests, 7, ("route", "/b"), ("status", "200"));

        var byRoute = store.Aggregate(Requests.Key, Now.AddMinutes(-1), Now, ["route"])!;
        Assert.Equal(["/b", "/a"], byRoute.Groups.Select(g => g.Attributes[0].Value));
        Assert.Equal([7d, 3d], byRoute.Groups.Select(g => g.Total.Sum));
        Assert.Equal(2, byRoute.Groups[1].Series);

        var errors = store.Aggregate(Requests.Key, Now.AddMinutes(-1), Now, [], a => a.Any(x => x is { Key: "status", Value: "500" }))!;
        Assert.Equal(2, Assert.Single(errors.Groups).Total.Sum);

        var top = store.Aggregate(Requests.Key, Now.AddMinutes(-1), Now, ["route"], maxGroups: 1)!;
        Assert.Single(top.Groups);
        Assert.Equal(1, top.HiddenGroups);
    }

    [Fact]
    public void Gauge_KeepsAverageMinMaxAndLast()
    {
        using var store = Create();
        foreach (var value in new[] { 10d, 30d, 20d })
        {
            Record(store, Memory, value);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        var minute = Assert.Single(store.Aggregate(Memory.Key, Now.AddHours(-2), Now, [])!.Groups);
        Assert.Equal(20, minute.Total.Average);
        Assert.Equal(10, minute.Total.Min);
        Assert.Equal(30, minute.Total.Max);
        Assert.Equal(20, minute.Total.Last);
    }

    [Fact]
    public void Histogram_GivesPercentilesOfMergedSeries()
    {
        using var store = Create();
        // 100 requests: 50 below 0.1 s, 40 up to 0.5 s, 10 up to 1 s.
        Record(store, Duration, new Measurement(20, 60, 0.01, 0.9, Bounds, [50, 10, 0, 0]), ("route", "/a"));
        Record(store, Duration, new Measurement(15, 40, 0.2, 1, Bounds, [0, 30, 10, 0]), ("route", "/b"));

        var group = Assert.Single(store.Aggregate(Duration.Key, Now.AddMinutes(-1), Now, [])!.Groups);
        Assert.Equal(100, group.Total.Count);
        Assert.Equal(0.35, MetricStats.Value(MetricKind.Histogram, "avg", group.Total, 10, Bounds));
        Assert.Equal(0.1, MetricStats.Value(MetricKind.Histogram, "p50", group.Total, 10, Bounds)!.Value, 6);
        Assert.Equal(0.5 + 0.5 * 0.5, MetricStats.Value(MetricKind.Histogram, "p95", group.Total, 10, Bounds)!.Value, 6);
        Assert.Equal(1, MetricStats.Value(MetricKind.Histogram, "max", group.Total, 10, Bounds));
        Assert.Equal(10, MetricStats.Value(MetricKind.Histogram, "rate", group.Total, 10, Bounds));
    }

    [Fact]
    public void Quantile_StaysWithinTheMeasuredRange()
    {
        var bucket = new MetricBucket { Count = 3, Min = 2, Max = 3, Histogram = [0, 0, 0, 3] };

        // All above the last bound: between the minimum and the maximum, not at the bound.
        Assert.Equal(2.99, MetricStats.Quantile(Bounds, bucket, 0.99)!.Value, 6);
        Assert.Equal(2.01, MetricStats.Quantile(Bounds, bucket, 0.01)!.Value, 6);
    }

    [Fact]
    public void CumulativeCounter_KeepsItsIncrease_AndSurvivesAReset()
    {
        using var store = Create();
        foreach (var total in new[] { 100d, 105d, 112d, 3d })
        {
            store.Record(Requests, [], Now, new Measurement(total), cumulative: true);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(5 + 7 + 3, Assert.Single(store.Aggregate(Requests.Key, Now.AddMinutes(-5), Now, [])!.Groups).Total.Sum);
    }

    [Fact]
    public void CumulativeHistogram_KeepsIncreases_AndSurvivesAReset()
    {
        using var store = Create();
        // Running totals, as an OTLP exporter sends them: 10, then 25, then (after a restart) 4 measurements.
        foreach (var (count, sum, buckets) in new[] { (10L, 2.0, new long[] { 6, 4, 0, 0 }), (25L, 6.0, [10, 12, 3, 0]), (4L, 1.0, [4, 0, 0, 0]) })
        {
            store.Record(Duration, [], Now, new Measurement(sum, count, 0.01, 0.9, Bounds, buckets), cumulative: true);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        var total = Assert.Single(store.Aggregate(Duration.Key, Now.AddMinutes(-5), Now, [])!.Groups).Total;
        Assert.Equal(15 + 4, total.Count);
        Assert.Equal(4 + 1, total.Sum, 6);
        Assert.Equal([4L + 4, 8, 3, 0], total.Histogram!);
        // The minimum and maximum of a running total cover the whole process, not a period.
        Assert.True(double.IsInfinity(total.Min));
    }

    [Fact]
    public void SeriesOverTheLimit_AreCounted()
    {
        using var store = Create(o => o.MaxSeriesPerInstrument = 2);
        for (var i = 0; i < 5; i++)
            Record(store, Requests, 1, ("user", $"u{i}"));

        var stats = Assert.Single(store.List());
        Assert.Equal(2, stats.Series);
        Assert.Equal(3, stats.DroppedSeries);
        Assert.Equal([new KeyValuePair<string, int>("user", 2)], stats.AttributeKeys);
    }

    [Fact]
    public void MemoryLimit_StopsNewSeries()
    {
        using var store = Create(o => o.MaxTotalBytes = 1);

        Record(store, Requests, 1);

        Assert.Equal(0, store.GetStats().Series);
        Assert.Equal(1, store.GetStats().DroppedSeries);
    }

    [Fact]
    public void StaleSeries_AreRemoved()
    {
        using var store = Create();
        Record(store, Requests, 1);

        _time.Advance(TimeSpan.FromDays(8));

        Assert.Equal((0, 0), (store.GetStats().Instruments, store.GetStats().Series));
        Assert.Equal(0, store.GetStats().Bytes);
    }

    [Fact]
    public void OldPeriods_AreOverwritten()
    {
        using var store = Create();
        Record(store, Requests, 1);
        _time.Advance(TimeSpan.FromHours(1));
        Record(store, Requests, 2);

        var hour = Assert.Single(store.Aggregate(Requests.Key, Now.AddMinutes(-59), Now, [])!.Groups);
        Assert.Equal(2, hour.Total.Sum);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private MemoryMetricStore Create(Action<MetricStoreOptions>? configure = null)
    {
        var options = new MetricStoreOptions();
        configure?.Invoke(options);
        return new MemoryMetricStore(options, _time);
    }

    private void Record(MemoryMetricStore store, MetricInstrument instrument, double value, params (string Key, string Value)[] attributes) =>
        Record(store, instrument, new Measurement(value), attributes);

    private void Record(MemoryMetricStore store, MetricInstrument instrument, Measurement value, params (string Key, string Value)[] attributes) =>
        store.Record(instrument, attributes.Select(a => new KeyValuePair<string, string?>(a.Key, a.Value)).ToArray(), Now, value);
}
