using Kododo.MonitorWay.Core.Model;

namespace Kododo.MonitorWay.Memory;

/// <summary>A resolution the series are kept in: buckets of <see cref="Seconds"/>, the last <see cref="Buckets"/> of them.</summary>
internal readonly record struct Resolution(int Seconds, int Buckets)
{
    public TimeSpan Span => TimeSpan.FromSeconds((double)Seconds * Buckets);
}

/// <summary>One measurement of a series as the SDK reported it at the end of a collection.</summary>
/// <param name="Value">The sum (its increase for a delta counter) or the gauge's value.</param>
/// <param name="Count">Histogram: measurements in the period.</param>
/// <param name="Bounds">Histogram: upper bounds of the buckets; <paramref name="BucketCounts"/> has one more, above the last bound.</param>
internal readonly record struct Measurement(
    double Value, long Count = 0, double? Min = null, double? Max = null, double[]? Bounds = null, long[]? BucketCounts = null);

/// <summary>What one series, or a group of series, measured in one period.</summary>
internal sealed class MetricBucket
{
    public long Index = -1;
    public double Sum;
    public long Count;
    public double Min = double.PositiveInfinity;
    public double Max = double.NegativeInfinity;
    public double Last;

    /// <summary>A gauge: the sum of the averages of the series in the period (for one series, its average).</summary>
    public double Average;

    public long[]? Histogram;

    public void Reset(long index)
    {
        Index = index;
        Sum = 0;
        Count = 0;
        Min = double.PositiveInfinity;
        Max = double.NegativeInfinity;
        Last = 0;
        Average = 0;
        if (Histogram is not null)
            Array.Clear(Histogram);
    }
}

/// <param name="Attributes">The values of the attributes the series are grouped by, or all attributes of an ungrouped series.</param>
/// <param name="Buckets">Per period from the start of the timeline; <c>null</c> where the group has no data.</param>
/// <param name="Total">The whole range merged.</param>
internal sealed record MetricGroup(string Key, KeyValuePair<string, string?>[] Attributes, MetricBucket?[] Buckets, MetricBucket Total, int Series);

/// <param name="Start">Start of the first period.</param>
/// <param name="HiddenGroups">Groups left out beyond the largest ones.</param>
internal sealed record MetricTimeline(
    MetricInstrument Instrument, Resolution Resolution, DateTimeOffset Start, int Length, IReadOnlyList<MetricGroup> Groups,
    int HiddenGroups, double[]? Bounds);

/// <param name="AttributeKeys">Keys of the attributes of the series, with how many values each has.</param>
internal sealed record MetricInstrumentStats(
    MetricInstrument Instrument, int Series, long DroppedSeries, DateTimeOffset LastUpdate,
    IReadOnlyList<KeyValuePair<string, int>> AttributeKeys);

/// <summary>
/// The metrics of the application as time series: per instrument and set of attribute values, in three resolutions
/// (<see cref="Resolutions"/>). Counters keep their increase per period, gauges and up-down counters their value
/// (average, minimum, maximum, last), histograms their counts per bucket, which give the percentiles of any period or
/// of several series merged.
/// </summary>
internal sealed class MemoryMetricStore : IDisposable
{
    public static readonly Resolution[] Resolutions = [new(10, 360), new(60, 1440), new(600, 1008)];

    private static readonly TimeSpan KeepSeriesFor = Resolutions[^1].Span;

    private readonly object _lock = new();
    private readonly Dictionary<string, InstrumentData> _instruments = [];
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private long _totalBytes;

    public MemoryMetricStore(MetricStoreOptions options, TimeProvider time)
    {
        options.Validate();
        MaxSeriesPerInstrument = options.MaxSeriesPerInstrument;
        MaxTotalBytes = options.MaxTotalBytes;
        ExportInterval = options.ExportInterval;
        _time = time;
        _timer = time.CreateTimer(_ => RemoveStale(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public int MaxSeriesPerInstrument { get; }
    public long MaxTotalBytes { get; }
    public TimeSpan ExportInterval { get; }

    /// <summary>Whether the OpenTelemetry SDK exports metrics to the store.</summary>
    public bool IsExportedTo { get; set; }

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Records a measurement of a series.</summary>
    /// <param name="cumulative">
    /// The value is a running total (a counter or histogram of a cumulative reader, as OTLP exporters send by default);
    /// its increase is kept.
    /// </param>
    public void Record(
        MetricInstrument instrument, KeyValuePair<string, string?>[] attributes, DateTimeOffset time, Measurement value,
        bool cumulative = false)
    {
        var key = SeriesKey(attributes);
        lock (_lock)
        {
            if (!_instruments.TryGetValue(instrument.Key, out var data))
                _instruments[instrument.Key] = data = new InstrumentData(instrument);
            data.LastUpdate = time;

            if (!data.Series.TryGetValue(key, out var series))
            {
                var bytes = SeriesBytes(value.BucketCounts?.Length ?? 0);
                if (data.Series.Count >= MaxSeriesPerInstrument || _totalBytes + bytes > MaxTotalBytes)
                {
                    data.DroppedSeries++;
                    return;
                }
                data.Series[key] = series = new SeriesData(attributes, bytes);
                _totalBytes += bytes;
            }
            series.LastUpdate = time;

            var measured = value.Value;
            if (cumulative && instrument.Kind == MetricKind.Counter)
            {
                var previous = series.Previous;
                series.Previous = measured;
                if (previous is null)
                    return;
                // A total lower than before was reset, e.g. by a restart of what reports it.
                measured = measured >= previous ? measured - previous.Value : measured;
            }

            if (instrument.Kind == MetricKind.Histogram)
            {
                series.Bounds ??= value.Bounds;
                if (cumulative && Increase(series, ref value) is false)
                    return;
                measured = value.Value;
            }

            var unix = time.ToUnixTimeSeconds();
            foreach (var ring in series.Rings)
            {
                var bucket = ring.At(unix / ring.Resolution.Seconds, create: true)!;
                Apply(instrument.Kind, bucket, measured, value, series.Bounds);
            }
        }
    }

    /// <summary>
    /// Turns a running total of a histogram (what OTLP exporters send by default) into what happened since the previous
    /// one; <c>false</c> for the first, which has nothing to compare with. Minimum and maximum of a running total cover the
    /// whole life of the process, so they are dropped.
    /// </summary>
    private static bool Increase(SeriesData series, ref Measurement value)
    {
        var previous = series.PreviousHistogram;
        series.PreviousHistogram = value;
        if (previous is not { } before)
            return false;

        // Fewer measurements than before: the total was reset (the process restarted), so it is all new.
        if (value.Count < before.Count)
        {
            value = value with { Min = null, Max = null };
            return true;
        }
        long[]? counts = null;
        if (value.BucketCounts is { } after && before.BucketCounts is { } earlier && after.Length == earlier.Length)
        {
            counts = new long[after.Length];
            for (var i = 0; i < after.Length; i++)
                counts[i] = Math.Max(0, after[i] - earlier[i]);
        }
        value = value with
        {
            Value = value.Value - before.Value,
            Count = value.Count - before.Count,
            Min = null,
            Max = null,
            BucketCounts = counts,
        };
        return true;
    }

    private static void Apply(MetricKind kind, MetricBucket bucket, double measured, Measurement value, double[]? bounds)
    {
        switch (kind)
        {
            case MetricKind.Counter:
                bucket.Sum += measured;
                bucket.Count++;
                break;
            case MetricKind.Histogram:
                bucket.Sum += measured;
                bucket.Count += value.Count;
                if (value.Count > 0 && value.Min is { } min && value.Max is { } max)
                {
                    bucket.Min = Math.Min(bucket.Min, min);
                    bucket.Max = Math.Max(bucket.Max, max);
                }
                if (value.BucketCounts is { } counts && bounds is not null && counts.Length == bounds.Length + 1)
                {
                    bucket.Histogram ??= new long[counts.Length];
                    for (var i = 0; i < counts.Length; i++)
                        bucket.Histogram[i] += counts[i];
                }
                break;
            default:
                bucket.Sum += measured;
                bucket.Count++;
                bucket.Last = measured;
                bucket.Min = Math.Min(bucket.Min, measured);
                bucket.Max = Math.Max(bucket.Max, measured);
                bucket.Average = bucket.Sum / bucket.Count;
                break;
        }
    }

    /// <summary>The instruments, by meter and name.</summary>
    public IReadOnlyList<MetricInstrumentStats> List()
    {
        lock (_lock)
        {
            return _instruments.Values
                .OrderBy(i => i.Info.Meter, StringComparer.Ordinal).ThenBy(i => i.Info.Name, StringComparer.Ordinal)
                .Select(i => new MetricInstrumentStats(i.Info, i.Series.Count, i.DroppedSeries, i.LastUpdate,
                    i.Series.Values.SelectMany(s => s.Attributes)
                        .GroupBy(a => a.Key)
                        .Select(g => new KeyValuePair<string, int>(g.Key, g.Select(a => a.Value).Distinct().Count()))
                        .OrderBy(k => k.Key, StringComparer.Ordinal).ToArray()))
                .ToArray();
        }
    }

    public MetricInstrument? FindInstrument(string key)
    {
        lock (_lock)
            return _instruments.TryGetValue(key, out var data) ? data.Info : null;
    }

    /// <summary>The instruments with the name, of any meter (e.g. <c>http.server.request.duration</c>).</summary>
    public IReadOnlyList<MetricInstrument> FindByName(string name)
    {
        lock (_lock)
            return _instruments.Values.Where(i => i.Info.Name == name).Select(i => i.Info).ToArray();
    }

    /// <summary>
    /// The series of an instrument within a range, merged into groups by the values of <paramref name="groupBy"/> (all
    /// into one when it is empty), in the finest resolution that still covers the range.
    /// </summary>
    /// <param name="filter">Which series to include, by their attributes.</param>
    /// <param name="maxGroups">The largest groups are returned; the others are counted.</param>
    public MetricTimeline? Aggregate(
        string instrumentKey, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> groupBy,
        Func<KeyValuePair<string, string?>[], bool>? filter = null, int maxGroups = 10)
    {
        var now = _time.GetUtcNow();
        if (to > now)
            to = now;
        // "The last hour" asked a moment ago still fits the hour of 10-second periods.
        var index = Array.FindIndex(Resolutions, r => from >= now - r.Span - TimeSpan.FromSeconds(r.Seconds));
        var resolution = Resolutions[index < 0 ? Resolutions.Length - 1 : index];
        if (from < now - resolution.Span)
            from = now - resolution.Span;

        var first = from.ToUnixTimeSeconds() / resolution.Seconds;
        var last = Math.Max(first, to.ToUnixTimeSeconds() / resolution.Seconds);
        var length = (int)Math.Min(last - first + 1, resolution.Buckets);
        first = last - length + 1;

        lock (_lock)
        {
            if (!_instruments.TryGetValue(instrumentKey, out var data))
                return null;
            var kind = data.Info.Kind;
            var ring = Array.IndexOf(Resolutions, resolution);
            var groups = new Dictionary<string, (KeyValuePair<string, string?>[] Attributes, MetricBucket?[] Buckets, int Series)>();
            double[]? bounds = null;

            foreach (var series in data.Series.Values)
            {
                if (filter is not null && !filter(series.Attributes))
                    continue;
                bounds ??= series.Bounds;
                var groupAttributes = groupBy.Count == 0
                    ? []
                    : groupBy.Select(k => new KeyValuePair<string, string?>(k, series.Attributes.FirstOrDefault(a => a.Key == k).Value)).ToArray();
                var key = SeriesKey(groupAttributes);
                if (!groups.TryGetValue(key, out var group))
                    group = (groupAttributes, new MetricBucket?[length], 0);
                groups[key] = group with { Series = group.Series + 1 };

                for (var i = 0; i < length; i++)
                {
                    if (series.Rings[ring].At(first + i, create: false) is not { } bucket)
                        continue;
                    var merged = group.Buckets[i] ??= new MetricBucket { Index = first + i };
                    Merge(kind, merged, bucket, ReferenceEquals(series.Bounds, bounds) || SameBounds(series.Bounds, bounds));
                }
            }

            var result = groups.Select(g => new MetricGroup(g.Key, g.Value.Attributes, g.Value.Buckets, Total(kind, g.Value.Buckets), g.Value.Series))
                .OrderByDescending(g => Weight(kind, g.Total))
                .ToList();
            var shown = result.Take(maxGroups).ToArray();
            return new MetricTimeline(
                data.Info, resolution, DateTimeOffset.FromUnixTimeSeconds(first * resolution.Seconds), length, shown,
                result.Count - shown.Length, bounds);
        }
    }

    public (int Instruments, int Series, long DroppedSeries, long Bytes) GetStats()
    {
        lock (_lock)
            return (_instruments.Count, _instruments.Values.Sum(i => i.Series.Count), _instruments.Values.Sum(i => i.DroppedSeries), _totalBytes);
    }

    /// <summary>Removes the series that have not been measured for longer than the coarsest resolution keeps.</summary>
    public void RemoveStale()
    {
        var limit = _time.GetUtcNow() - KeepSeriesFor;
        lock (_lock)
        {
            foreach (var data in _instruments.Values.ToArray())
            {
                foreach (var (key, series) in data.Series.ToArray())
                {
                    if (series.LastUpdate >= limit)
                        continue;
                    data.Series.Remove(key);
                    _totalBytes -= series.Bytes;
                }
                if (data.Series.Count == 0 && data.LastUpdate < limit)
                    _instruments.Remove(data.Info.Key);
            }
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Merges a series' bucket into the bucket of its group.</summary>
    private static void Merge(MetricKind kind, MetricBucket into, MetricBucket from, bool sameBounds)
    {
        into.Sum += from.Sum;
        into.Count += from.Count;
        into.Min = Math.Min(into.Min, from.Min);
        into.Max = Math.Max(into.Max, from.Max);
        into.Last += from.Last;
        into.Average += from.Average;
        if (kind == MetricKind.Histogram && from.Histogram is { } histogram && sameBounds)
        {
            into.Histogram ??= new long[histogram.Length];
            if (into.Histogram.Length == histogram.Length)
            {
                for (var i = 0; i < histogram.Length; i++)
                    into.Histogram[i] += histogram[i];
            }
        }
    }

    /// <summary>The periods of a group merged: sums add up, a gauge's average is that of the periods, its last value the latest.</summary>
    private static MetricBucket Total(MetricKind kind, MetricBucket?[] buckets)
    {
        var total = new MetricBucket();
        var periods = 0;
        foreach (var bucket in buckets)
        {
            if (bucket is null)
                continue;
            periods++;
            total.Sum += bucket.Sum;
            total.Count += bucket.Count;
            total.Min = Math.Min(total.Min, bucket.Min);
            total.Max = Math.Max(total.Max, bucket.Max);
            total.Last = bucket.Last;
            total.Average += bucket.Average;
            if (bucket.Histogram is { } histogram)
            {
                total.Histogram ??= new long[histogram.Length];
                if (total.Histogram.Length == histogram.Length)
                {
                    for (var i = 0; i < histogram.Length; i++)
                        total.Histogram[i] += histogram[i];
                }
            }
        }
        if (periods > 0 && kind is MetricKind.Gauge or MetricKind.UpDownCounter)
            total.Average /= periods;
        return total;
    }

    private static double Weight(MetricKind kind, MetricBucket total) => kind switch
    {
        MetricKind.Counter => total.Sum,
        MetricKind.Histogram => total.Count,
        _ => Math.Abs(total.Average),
    };

    private static bool SameBounds(double[]? a, double[]? b) =>
        a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Memory a series may take once its periods are all filled, reserved when it is created.</summary>
    private static long SeriesBytes(int histogramBuckets)
    {
        const int BucketBytes = 96;
        var perBucket = BucketBytes + 8 + (histogramBuckets > 0 ? 24 + histogramBuckets * 8L : 0);
        return 256 + Resolutions.Sum(r => r.Buckets * perBucket);
    }

    private static string SeriesKey(KeyValuePair<string, string?>[] attributes) =>
        string.Join('\u001f', attributes.Select(a => $"{a.Key}\u001e{a.Value}"));

    private sealed class InstrumentData(MetricInstrument info)
    {
        public MetricInstrument Info { get; } = info;
        public Dictionary<string, SeriesData> Series { get; } = [];
        public long DroppedSeries { get; set; }
        public DateTimeOffset LastUpdate { get; set; }
    }

    private sealed class SeriesData(KeyValuePair<string, string?>[] attributes, long bytes)
    {
        public KeyValuePair<string, string?>[] Attributes { get; } = attributes;
        public long Bytes { get; } = bytes;
        public Ring[] Rings { get; } = Resolutions.Select(r => new Ring(r)).ToArray();
        public double[]? Bounds { get; set; }
        public double? Previous { get; set; }
        public Measurement? PreviousHistogram { get; set; }
        public DateTimeOffset LastUpdate { get; set; }
    }

    private sealed class Ring(Resolution resolution)
    {
        private MetricBucket?[]? _buckets;

        public Resolution Resolution { get; } = resolution;

        /// <summary>The bucket of the period, or <c>null</c> when it has no data (and <paramref name="create"/> is not set).</summary>
        public MetricBucket? At(long index, bool create)
        {
            if (_buckets is null)
            {
                if (!create)
                    return null;
                _buckets = new MetricBucket?[Resolution.Buckets];
            }
            var slot = (int)(index % Resolution.Buckets);
            var bucket = _buckets[slot];
            if (bucket?.Index == index)
                return bucket;
            if (!create)
                return null;
            // An older period in the slot is overwritten.
            bucket ??= _buckets[slot] = new MetricBucket();
            bucket.Reset(index);
            return bucket;
        }
    }
}
