using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Recent traces of the application. Spans come one by one as they end; they are gathered per trace and the trace is
/// stored whole once it is complete: its root ended and <see cref="TraceStoreOptions.CompletionDelay"/> passed.
/// </summary>
/// <remarks>
/// Deciding after completion (tail-based) is what lets the store keep failed and slow traces longer than the rest:
/// whether a trace failed is known only when all its spans are in. A trace whose root never arrives, e.g. one that
/// started in another service and was not sampled there, is stored as it is after <see cref="MaxPendingTime"/>.
/// </remarks>
internal sealed class MemoryTraceStore : IDisposable
{
    /// <summary>More spans of one trace are counted, not kept.</summary>
    internal const int MaxSpansPerTrace = 1_000;

    /// <summary>Traces being received at once; above it the oldest is stored as it is.</summary>
    internal const int MaxPendingTraces = 10_000;

    internal static readonly TimeSpan MaxPendingTime = TimeSpan.FromMinutes(2);

    /// <summary>How long the spans of an ignored trace (a request to the UI) are still recognized and dropped.</summary>
    private static readonly TimeSpan IgnoredFor = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Dictionary<string, (TraceEntry Entry, TracePool Pool, int Size)> _stored = [];
    private readonly Dictionary<string, DateTimeOffset> _ignored = [];
    private readonly TracePool _errors;
    private readonly TracePool _slow;
    private readonly TracePool _other;
    private long _lastId;
    private long _totalBytes;

    public MemoryTraceStore(
        TraceStoreOptions options, TimeProvider time, IgnoredRequests? ignoredRequests = null, MemoryErrorStore? errors = null)
    {
        Errors = errors;
        options.Validate();
        _time = time;
        MaxTotalBytes = options.MaxTotalBytes;
        SlowThreshold = options.SlowThreshold;
        CompletionDelay = options.CompletionDelay;
        IgnoredRequests = ignoredRequests ?? new IgnoredRequests();
        _errors = new TracePool("errors", options.Errors);
        _slow = new TracePool("slow", options.Slow);
        _other = new TracePool("other", options.Other);
        Pools = [_other, _slow, _errors];
        _timer = time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public long MaxTotalBytes { get; }
    public TimeSpan SlowThreshold { get; }
    public TimeSpan CompletionDelay { get; }

    /// <summary>The pools in the order of eviction under <see cref="MaxTotalBytes"/>: other, slow, errors.</summary>
    public IReadOnlyList<TracePool> Pools { get; }

    /// <summary>Requests whose traces are dropped; see <see cref="Memory.IgnoredRequests"/>.</summary>
    public IgnoredRequests IgnoredRequests { get; }

    /// <summary>Where the exceptions recorded on spans go, as the traces are completed.</summary>
    public MemoryErrorStore? Errors { get; }

    /// <summary>Whether the OpenTelemetry SDK exports traces to the store.</summary>
    public bool IsExportedTo { get; set; }

    /// <summary>Adds an ended span to its trace.</summary>
    /// <param name="size">Approximate memory the span takes, for the limits.</param>
    public void Add(TraceSpan span, int size)
    {
        // The server span of a request to the UI; its children ended before it and are dropped with the trace.
        var ignored = span.Kind == TraceSpanKind.Server &&
                      IgnoredRequests.MatchesPath(span.Attribute("url.path") ?? span.Attribute("http.target"));
        var now = _time.GetUtcNow();

        lock (_lock)
        {
            if (_ignored.ContainsKey(span.TraceId))
                return;
            if (ignored)
            {
                _pending.Remove(span.TraceId);
                _ignored[span.TraceId] = now;
                return;
            }

            if (!_pending.TryGetValue(span.TraceId, out var pending))
            {
                if (_pending.Count >= MaxPendingTraces)
                    Complete(_pending.Values.MinBy(p => p.FirstSeen)!, now);
                _pending[span.TraceId] = pending = new Pending(span.TraceId, now);
            }

            if (pending.Spans.Count < MaxSpansPerTrace)
            {
                pending.Spans.Add(span);
                pending.Bytes += size;
            }
            else
            {
                pending.Dropped++;
            }

            if (span.ParentSpanId is null || span.HasRemoteParent)
            {
                pending.RootEnded = now;
                if (CompletionDelay <= TimeSpan.Zero)
                    Complete(pending, now);
            }
        }
    }

    /// <summary>Stores the traces that are complete and removes those that expired; runs every second.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            List<Pending>? due = null;
            foreach (var pending in _pending.Values)
            {
                if (pending.RootEnded + CompletionDelay <= now || pending.FirstSeen + MaxPendingTime <= now)
                    (due ??= []).Add(pending);
            }
            if (due is not null)
            {
                foreach (var pending in due.OrderBy(p => p.FirstSeen))
                    Complete(pending, now);
            }

            foreach (var pool in Pools)
            {
                while (pool.IsOldestExpired(now))
                    Forget(pool.RemoveOldest());
            }

            if (_ignored.Count > 0)
            {
                foreach (var (traceId, since) in _ignored.ToArray())
                {
                    if (since + IgnoredFor <= now)
                        _ignored.Remove(traceId);
                }
            }
        }
    }

    /// <summary>
    /// The trace with the id, including the spans still being received, or <c>null</c> when there is none.
    /// </summary>
    public TraceEntry? Find(string traceId)
    {
        lock (_lock)
        {
            var hasStored = _stored.TryGetValue(traceId, out var stored);
            if (!_pending.TryGetValue(traceId, out var pending))
                return hasStored ? stored.Entry : null;

            IEnumerable<TraceSpan> spans = pending.Spans;
            if (hasStored)
                spans = stored.Entry.Spans.Concat(spans);
            return new TraceEntry(
                hasStored ? stored.Entry.Id : 0, traceId, spans.ToArray(), isComplete: false,
                pending.Dropped + (hasStored ? stored.Entry.DroppedSpans : 0));
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> matching stored traces with an id lower than <paramref name="beforeId"/>
    /// (from the newest when it is <c>null</c>), newest first.
    /// </summary>
    public IReadOnlyList<TraceEntry> ReadBefore(long? beforeId, int count, TraceQueryMatcher matcher, out bool hasMore)
    {
        TraceEntry[][] pools;
        lock (_lock)
            pools = Pools.Select(p => p.Snapshot()).ToArray();

        // Each pool is ordered by id; walk them together from the end.
        var positions = pools.Select(p => p.Length - 1).ToArray();
        var limit = beforeId ?? long.MaxValue;
        var result = new List<TraceEntry>(Math.Min(count, 256));
        hasMore = false;

        while (true)
        {
            var best = -1;
            for (var i = 0; i < pools.Length; i++)
            {
                while (positions[i] >= 0 && pools[i][positions[i]].Id >= limit)
                    positions[i]--;
                if (positions[i] >= 0 && (best < 0 || pools[i][positions[i]].Id > pools[best][positions[best]].Id))
                    best = i;
            }
            if (best < 0)
                return result;

            var entry = pools[best][positions[best]--];
            if (!matcher.Matches(entry))
                continue;
            if (result.Count == count)
            {
                hasMore = true;
                return result;
            }
            result.Add(entry);
        }
    }

    public TraceStoreStats GetStats()
    {
        lock (_lock)
        {
            return new TraceStoreStats(
                Pools.Select(p => new TracePoolStats(p.Name, p.Count, p.Bytes, p.OldestStored, p.Evicted, p.MaxTraces, p.MaxBytes, p.MaxAge)).ToArray(),
                _pending.Count, _totalBytes, MaxTotalBytes, _lastId);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Complete(Pending pending, DateTimeOffset now)
    {
        _pending.Remove(pending.TraceId);
        if (pending.Spans.Count == 0)
            return;

        IReadOnlyList<TraceSpan> spans = pending.Spans;
        var dropped = pending.Dropped;
        var size = pending.Bytes;

        // Late spans of a trace stored before: the trace is stored again with all of them, under a new id.
        if (_stored.TryGetValue(pending.TraceId, out var earlier))
        {
            earlier.Pool.Remove(earlier.Entry.Id);
            _totalBytes -= earlier.Size;
            _stored.Remove(pending.TraceId);

            var all = earlier.Entry.Spans.Concat(spans).ToList();
            dropped += earlier.Entry.DroppedSpans + Math.Max(0, all.Count - MaxSpansPerTrace);
            spans = all.Count > MaxSpansPerTrace ? all.GetRange(0, MaxSpansPerTrace) : all;
            size += earlier.Size;
        }

        var entry = new TraceEntry(++_lastId, pending.TraceId, spans, isComplete: true, dropped);
        if (Errors is not null)
            ReportExceptions(pending.Spans, entry.Spans);
        var pool = entry.HasError ? _errors : entry.Duration >= SlowThreshold ? _slow : _other;
        pool.Append(entry, size, now);
        _stored[entry.TraceId] = (entry, pool, size);
        _totalBytes += size;

        while (pool.IsOverLimit || pool.IsOldestExpired(now))
            Forget(pool.RemoveOldest());
        while (_totalBytes > MaxTotalBytes && Pools.FirstOrDefault(p => p.Count > 0) is { } victim)
            Forget(victim.RemoveOldest());
    }

    /// <summary>
    /// Reports the <c>exception</c> events of the spans received since the trace was last stored; those of a trace that is
    /// ignored never get here.
    /// </summary>
    private void ReportExceptions(List<TraceSpan> received, IReadOnlyList<TraceSpan> all)
    {
        Dictionary<string, TraceSpan>? byId = null;
        foreach (var span in received)
        {
            foreach (var e in span.Events)
            {
                if (e.Name != "exception")
                    continue;
                byId ??= all.GroupBy(s => s.SpanId).ToDictionary(g => g.Key, g => g.First());
                string? Attribute(string key) => e.Attributes.FirstOrDefault(a => a.Key == key).Value;

                var type = Attribute("exception.type") ?? "Exception";
                var message = Attribute("exception.message") ?? "";
                Errors!.Add(new ReportedException(
                    e.Timestamp, type, message, Attribute("exception.stacktrace") ?? $"{type}: {message}", span.ServiceName,
                    LocalRoot(span, byId).Name, span.TraceId, span.SpanId, FromSpan: true));
            }
        }
    }

    /// <summary>The span the application started for the work: the top of the span's chain within this process.</summary>
    private static TraceSpan LocalRoot(TraceSpan span, Dictionary<string, TraceSpan> byId)
    {
        for (var depth = 0; depth < 256 && !span.HasRemoteParent && span.ParentSpanId is { } parentId && byId.TryGetValue(parentId, out var parent); depth++)
            span = parent;
        return span;
    }

    private void Forget((TraceEntry Entry, int Size) removed)
    {
        _totalBytes -= removed.Size;
        if (_stored.TryGetValue(removed.Entry.TraceId, out var stored) && stored.Entry == removed.Entry)
            _stored.Remove(removed.Entry.TraceId);
    }

    private sealed class Pending(string traceId, DateTimeOffset firstSeen)
    {
        public string TraceId { get; } = traceId;
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public DateTimeOffset? RootEnded { get; set; }
        public List<TraceSpan> Spans { get; } = [];
        public int Bytes { get; set; }
        public int Dropped { get; set; }
    }
}

internal sealed record TracePoolStats(
    string Name, int Count, long Bytes, DateTimeOffset? Oldest, long Evicted, int? MaxTraces, long? MaxBytes, TimeSpan? MaxAge);

internal sealed record TraceStoreStats(TracePoolStats[] Pools, int Pending, long TotalBytes, long MaxTotalBytes, long LastId);
