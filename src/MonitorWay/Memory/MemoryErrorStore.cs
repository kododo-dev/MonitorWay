using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Errors;
using Kododo.MonitorWay.Querying;

namespace Kododo.MonitorWay.Memory;

/// <summary>An exception as a log entry or a span event reported it.</summary>
/// <param name="Text">The exception as text: type, message and stack trace, with the inner exceptions.</param>
internal sealed record ReportedException(
    DateTimeOffset Timestamp,
    string Type,
    string Message,
    string? Text,
    string? ServiceName,
    string? Endpoint,
    string? TraceId,
    string? SpanId,
    long? LogEntryId = null,
    string? Category = null,
    bool FromSpan = false);

/// <summary>A group with its occurrences per hour, oldest hour first.</summary>
internal sealed record ErrorGroupView(ErrorGroup Group, int[] Trend);

/// <summary>
/// The exceptions of the application, grouped by <see cref="ErrorFingerprint"/>. Each group keeps counters, an hourly
/// trend for a week and its latest occurrences; the exception reported by both a log entry and a span event of one
/// trace is counted once.
/// </summary>
internal sealed class MemoryErrorStore : IDisposable
{
    /// <summary>Hours of the trend each group keeps.</summary>
    public const int TrendHours = 7 * 24;

    /// <summary>How far apart a log entry and a span event of one trace may be to be the same occurrence.</summary>
    private static readonly TimeSpan SameOccurrence = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, Group> _groups = [];
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    public MemoryErrorStore(ErrorStoreOptions options, TimeProvider time)
    {
        options.Validate();
        MaxGroups = options.MaxGroups;
        MaxOccurrences = options.MaxOccurrences;
        MaxAge = options.MaxAge;
        _time = time;
        _timer = time.CreateTimer(_ => RemoveExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int MaxGroups { get; }
    public int MaxOccurrences { get; }
    public TimeSpan? MaxAge { get; }

    /// <summary>Whether the OpenTelemetry SDK exports logs or traces to MonitorWay, which is where errors come from.</summary>
    public bool IsExportedTo { get; set; }

    public void Add(ReportedException report)
    {
        var (fingerprint, frames) = ErrorFingerprint.Compute(report.Type, report.Text, report.Endpoint, out var type);
        lock (_lock)
        {
            if (!_groups.TryGetValue(fingerprint, out var group))
            {
                if (_groups.Count >= MaxGroups)
                    _groups.Remove(_groups.Values.MinBy(g => g.LastSeen)!.Fingerprint);
                _groups[fingerprint] = group = new Group(fingerprint, type, frames, report.Timestamp);
            }

            if (group.TryMerge(report))
                return;
            group.Add(report, MaxOccurrences);
        }
    }

    public void RemoveExpired()
    {
        if (MaxAge is not { } maxAge)
            return;
        var limit = _time.GetUtcNow() - maxAge;
        lock (_lock)
        {
            foreach (var group in _groups.Values.Where(g => g.LastSeen < limit).ToArray())
                _groups.Remove(group.Fingerprint);
        }
    }

    /// <summary>The matching groups, the latest seen first or, with <paramref name="byCount"/>, the most frequent first.</summary>
    public IReadOnlyList<ErrorGroupView> Read(ErrorQueryMatcher matcher, bool byCount, int count, int trendHours = 24)
    {
        var now = _time.GetUtcNow();
        List<(ErrorGroup Group, int[] Trend)> all;
        lock (_lock)
            all = _groups.Values.Select(g => (g.Snapshot(matcher.NeedsOccurrences), g.Trend(now, trendHours))).ToList();

        var matching = all.Where(v => matcher.Matches(v.Group));
        matching = byCount
            ? matching.OrderByDescending(v => v.Group.Count).ThenByDescending(v => v.Group.LastSeen)
            : matching.OrderByDescending(v => v.Group.LastSeen);
        return matching.Take(count).Select(v => new ErrorGroupView(v.Group, v.Trend)).ToArray();
    }

    public ErrorGroupView? Find(string fingerprint, int trendHours = TrendHours)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
            return _groups.TryGetValue(fingerprint, out var group) ? new ErrorGroupView(group.Snapshot(), group.Trend(now, trendHours)) : null;
    }

    public (int Groups, long Occurrences) GetStats()
    {
        lock (_lock)
            return (_groups.Count, _groups.Values.Sum(g => g.Count));
    }

    public void Dispose() => _timer.Dispose();

    private sealed class Group(string fingerprint, string type, IReadOnlyList<string> frames, DateTimeOffset firstSeen)
    {
        private readonly List<ErrorOccurrence> _occurrences = [];
        private readonly HashSet<string> _services = [];
        private readonly Dictionary<string, int> _endpoints = [];
        private readonly int[] _hourCounts = new int[TrendHours];
        private readonly long[] _hours = new long[TrendHours];
        private string _message = "";
        private string? _text;

        public string Fingerprint { get; } = fingerprint;
        public long Count { get; private set; }
        public DateTimeOffset FirstSeen { get; private set; } = firstSeen;
        public DateTimeOffset LastSeen { get; private set; } = firstSeen;

        /// <summary>
        /// Joins the report to an occurrence of the same trace that the other kind of report made: the log entry and the
        /// span event of one exception.
        /// </summary>
        public bool TryMerge(ReportedException report)
        {
            if (report.TraceId is null)
                return false;
            for (var i = _occurrences.Count - 1; i >= 0; i--)
            {
                var o = _occurrences[i];
                if (o.TraceId != report.TraceId || (o.Timestamp - report.Timestamp).Duration() > SameOccurrence)
                    continue;
                var complements = report.FromSpan ? !o.FromSpan : o.LogEntryId is null && o.Category is null;
                if (!complements)
                    continue;

                _occurrences[i] = o with
                {
                    Timestamp = o.Timestamp < report.Timestamp ? o.Timestamp : report.Timestamp,
                    FromSpan = o.FromSpan || report.FromSpan,
                    LogEntryId = o.LogEntryId ?? report.LogEntryId,
                    Category = o.Category ?? report.Category,
                    // The span event's span is where the exception was recorded; the log's is where it was logged.
                    SpanId = report.FromSpan ? report.SpanId ?? o.SpanId : o.SpanId ?? report.SpanId,
                    // The span's is read when the trace is complete; a log entry may see the request's span before the
                    // instrumentation named it after its route ("GET" rather than "GET /orders/{id}").
                    Endpoint = report.FromSpan ? report.Endpoint ?? o.Endpoint : o.Endpoint ?? report.Endpoint,
                };
                if (_occurrences[i].Endpoint != o.Endpoint)
                {
                    if (o.Endpoint is { } before && _endpoints.TryGetValue(before, out var count))
                    {
                        if (count <= 1)
                            _endpoints.Remove(before);
                        else
                            _endpoints[before] = count - 1;
                    }
                    var after = _occurrences[i].Endpoint!;
                    _endpoints[after] = _endpoints.GetValueOrDefault(after) + 1;
                }
                if (report.Text is { Length: > 0 } text && (_text is null || text.Length > _text.Length))
                    _text = text;
                return true;
            }
            return false;
        }

        public void Add(ReportedException report, int maxOccurrences)
        {
            Count++;
            if (report.Timestamp < FirstSeen)
                FirstSeen = report.Timestamp;
            if (report.Timestamp >= LastSeen || Count == 1)
            {
                LastSeen = report.Timestamp;
                _message = report.Message;
                _text = report.Text ?? _text;
            }
            if (report.ServiceName is { } service)
                _services.Add(service);
            if (report.Endpoint is { } endpoint)
                _endpoints[endpoint] = _endpoints.GetValueOrDefault(endpoint) + 1;

            var hour = report.Timestamp.ToUnixTimeSeconds() / 3600;
            var slot = (int)(hour % TrendHours);
            if (_hours[slot] != hour)
            {
                _hours[slot] = hour;
                _hourCounts[slot] = 0;
            }
            _hourCounts[slot]++;

            _occurrences.Add(new ErrorOccurrence(
                report.Timestamp, report.Message, report.ServiceName, report.Endpoint, report.TraceId, report.SpanId,
                report.LogEntryId, report.Category, report.FromSpan));
            if (_occurrences.Count > maxOccurrences)
                _occurrences.RemoveAt(0);
        }

        /// <summary>Occurrences per hour for the last <paramref name="hours"/> hours, the current one last.</summary>
        public int[] Trend(DateTimeOffset now, int hours)
        {
            hours = Math.Clamp(hours, 1, TrendHours);
            var current = now.ToUnixTimeSeconds() / 3600;
            var trend = new int[hours];
            for (var i = 0; i < hours; i++)
            {
                var hour = current - (hours - 1 - i);
                var slot = (int)(((hour % TrendHours) + TrendHours) % TrendHours);
                trend[i] = _hours[slot] == hour ? _hourCounts[slot] : 0;
            }
            return trend;
        }

        public ErrorGroup Snapshot(bool withOccurrences = true) => new(
            Fingerprint, type, _message, _text, frames, Count, FirstSeen, LastSeen,
            _services.Order(StringComparer.Ordinal).ToArray(),
            _endpoints.OrderByDescending(e => e.Value).Select(e => e.Key).Take(10).ToArray(),
            withOccurrences ? Enumerable.Reverse(_occurrences).ToArray() : []);
    }
}
