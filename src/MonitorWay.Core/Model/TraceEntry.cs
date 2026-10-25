namespace Kododo.MonitorWay.Core.Model;

/// <summary>
/// A whole trace: every span of one request or operation that MonitorWay received.
/// </summary>
public sealed class TraceEntry
{
    /// <param name="id">
    /// Position of the trace in the store, increasing in the order traces were completed. A trace that received late
    /// spans after it was completed is stored again with a new id.
    /// </param>
    /// <param name="traceId">W3C trace id.</param>
    /// <param name="spans">The spans, in any order; the entry keeps them ordered by start.</param>
    /// <param name="isComplete">
    /// <c>false</c> while the trace is still being received: its root has not ended yet, or it ended moments ago and
    /// spans may still come.
    /// </param>
    /// <param name="droppedSpans">Spans left out because the trace had more than the store keeps per trace.</param>
    public TraceEntry(long id, string traceId, IReadOnlyList<TraceSpan> spans, bool isComplete = true, int droppedSpans = 0)
    {
        if (spans.Count == 0)
            throw new ArgumentException("A trace has at least one span.", nameof(spans));

        Id = id;
        TraceId = traceId;
        IsComplete = isComplete;
        DroppedSpans = droppedSpans;

        var ordered = spans.ToArray();
        Array.Sort(ordered, static (a, b) => a.Start.CompareTo(b.Start));
        Spans = ordered;

        var ids = new HashSet<string>(ordered.Select(s => s.SpanId));
        Root = Array.Find(ordered, s => s.ParentSpanId is null || !ids.Contains(s.ParentSpanId)) ?? ordered[0];
        Start = ordered[0].Start;
        Duration = ordered.Max(s => s.End) - Start;
        ErrorCount = ordered.Count(s => s.Status == TraceSpanStatus.Error);
        Services = ordered.Select(s => s.ServiceName).OfType<string>().Distinct().ToArray();
    }

    public long Id { get; }
    public string TraceId { get; }
    public IReadOnlyList<TraceSpan> Spans { get; }
    public bool IsComplete { get; }
    public int DroppedSpans { get; }

    /// <summary>
    /// The span the others run in: the one without a parent, or else the earliest whose parent did not reach
    /// MonitorWay (a trace that started in another service).
    /// </summary>
    public TraceSpan Root { get; }

    /// <summary>Start of the earliest span.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>From the start of the earliest span to the end of the latest.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Spans with the error status.</summary>
    public int ErrorCount { get; }

    public bool HasError => ErrorCount > 0;

    /// <summary>The services that recorded the spans, in the order they first appear.</summary>
    public IReadOnlyList<string> Services { get; }
}
