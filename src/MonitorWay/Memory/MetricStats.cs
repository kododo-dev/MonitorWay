using Kododo.MonitorWay.Core.Model;

namespace Kododo.MonitorWay.Memory;

/// <summary>Values read from the buckets of a series or group: rates, averages and percentiles.</summary>
internal static class MetricStats
{
    /// <summary>The statistics an instrument of the kind offers; the first is the default.</summary>
    public static IReadOnlyList<string> For(MetricKind kind) => kind switch
    {
        MetricKind.Counter => ["sum", "rate"],
        MetricKind.Histogram => ["p50", "p90", "p95", "p99", "avg", "max", "count", "rate"],
        _ => ["avg", "last", "min", "max"],
    };

    /// <summary>A statistic of a bucket, or <c>null</c> when the bucket has nothing to say about it.</summary>
    /// <param name="seconds">Length of the period, for rates.</param>
    public static double? Value(MetricKind kind, string stat, MetricBucket? bucket, double seconds, double[]? bounds)
    {
        if (bucket is null)
            return null;
        switch (kind)
        {
            case MetricKind.Counter:
                return stat == "rate" ? bucket.Sum / seconds : bucket.Sum;
            case MetricKind.Histogram:
                return stat switch
                {
                    "count" => bucket.Count,
                    "rate" => bucket.Count / seconds,
                    "avg" => bucket.Count > 0 ? bucket.Sum / bucket.Count : null,
                    "max" => bucket.Count > 0 && !double.IsInfinity(bucket.Max) ? bucket.Max : null,
                    ['p', ..] when double.TryParse(stat[1..], System.Globalization.CultureInfo.InvariantCulture, out var p) =>
                        Quantile(bounds, bucket, p / 100),
                    _ => null,
                };
            default:
                return stat switch
                {
                    "last" => bucket.Last,
                    "min" => double.IsInfinity(bucket.Min) ? null : bucket.Min,
                    "max" => double.IsInfinity(bucket.Max) ? null : bucket.Max,
                    _ => bucket.Average,
                };
        }
    }

    /// <summary>
    /// The value below which the fraction <paramref name="q"/> of the measurements fall, interpolated within its bucket
    /// and kept within the measured minimum and maximum.
    /// </summary>
    public static double? Quantile(double[]? bounds, MetricBucket bucket, double q)
    {
        if (bounds is null || bucket.Histogram is not { } counts || bucket.Count == 0)
            return null;
        var total = counts.Sum();
        if (total == 0)
            return null;

        var hasRange = !double.IsInfinity(bucket.Min) && !double.IsInfinity(bucket.Max);
        var rank = q * total;
        long seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0 || seen + counts[i] < rank)
            {
                seen += counts[i];
                continue;
            }
            var lower = i == 0 ? (hasRange ? Math.Min(bucket.Min, bounds[0]) : 0) : bounds[i - 1];
            var upper = i < bounds.Length ? bounds[i] : (hasRange ? bucket.Max : lower);
            if (hasRange)
            {
                lower = Math.Max(lower, bucket.Min);
                upper = Math.Min(upper, bucket.Max);
            }
            var value = lower + (upper - lower) * ((rank - seen) / counts[i]);
            return Math.Max(lower, Math.Min(upper, value));
        }
        return hasRange ? bucket.Max : bounds[^1];
    }
}
