using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class TraceStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Trace_IsStoredOnceItsRootEndedAndTheDelayPassed()
    {
        using var store = Create();
        store.Add(Span("t1", "c", "r"), 100);
        store.Add(Span("t1", "r", null), 100);

        Assert.Empty(Read(store));
        var pending = store.Find("t1")!;
        Assert.False(pending.IsComplete);
        Assert.Equal(2, pending.Spans.Count);

        _time.Advance(TimeSpan.FromSeconds(5));

        var trace = Assert.Single(Read(store));
        Assert.True(trace.IsComplete);
        Assert.Equal("r", trace.Root.SpanId);
        Assert.Equal(2, trace.Spans.Count);
    }

    [Fact]
    public void LateSpans_StoreTheTraceAgainWithAllSpans()
    {
        using var store = Create();
        store.Add(Span("t1", "r", null), 100);
        _time.Advance(TimeSpan.FromSeconds(5));
        var first = Assert.Single(Read(store));

        store.Add(Span("t1", "late", "r"), 100);
        _time.Advance(TimeSpan.FromMinutes(3));

        var again = Assert.Single(Read(store));
        Assert.True(again.Id > first.Id);
        Assert.Equal(["r", "late"], again.Spans.Select(s => s.SpanId));
        Assert.Equal(200, store.GetStats().TotalBytes);
    }

    [Fact]
    public void TraceWithoutRoot_IsStoredAfterTheMaximumWait()
    {
        using var store = Create();
        store.Add(Span("t1", "c", "elsewhere"), 100);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Read(store));

        _time.Advance(MemoryTraceStore.MaxPendingTime);
        var trace = Assert.Single(Read(store));
        Assert.Equal("c", trace.Root.SpanId);
    }

    [Fact]
    public void Traces_GoToPoolsByOutcome_AndAPoolKeepsItsLimit()
    {
        using var store = Create(o =>
        {
            o.SlowThreshold = TimeSpan.FromSeconds(1);
            o.Other.KeepTraces(2);
        });

        store.Add(Span("e1", "r", null, status: TraceSpanStatus.Error), 10);
        store.Add(Span("s1", "r", null, duration: TimeSpan.FromSeconds(2)), 10);
        for (var i = 0; i < 5; i++)
            store.Add(Span($"o{i}", "r", null), 10);
        _time.Advance(TimeSpan.FromSeconds(5));

        var pools = store.GetStats().Pools.ToDictionary(p => p.Name);
        Assert.Equal(1, pools["errors"].Count);
        Assert.Equal(1, pools["slow"].Count);
        Assert.Equal(2, pools["other"].Count);
        Assert.Equal(3, pools["other"].Evicted);
        Assert.Equal(["o4", "o3", "s1", "e1"], Read(store).Select(t => t.TraceId));
        Assert.Null(store.Find("o0"));
        Assert.NotNull(store.Find("e1"));
    }

    [Fact]
    public void TotalLimit_RemovesOtherTracesBeforeErrors()
    {
        using var store = Create(o => o.MaxTotalBytes = 300);

        store.Add(Span("e1", "r", null, status: TraceSpanStatus.Error), 100);
        _time.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
            store.Add(Span($"o{i}", "r", null), 100);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["o3", "o2", "e1"], Read(store).Select(t => t.TraceId));
        Assert.Equal(300, store.GetStats().TotalBytes);
    }

    [Fact]
    public void MaxAge_RemovesOldTraces()
    {
        using var store = Create(o => o.Other.KeepFor(TimeSpan.FromMinutes(10)));
        store.Add(Span("old", "r", null), 10);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(Read(store));

        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Empty(Read(store));
        Assert.Equal(0, store.GetStats().TotalBytes);
    }

    [Fact]
    public void SpansOverTheLimit_AreCounted()
    {
        using var store = Create();
        for (var i = 0; i < MemoryTraceStore.MaxSpansPerTrace + 5; i++)
            store.Add(Span("big", $"c{i}", "r"), 1);
        store.Add(Span("big", "r", null), 1);
        _time.Advance(TimeSpan.FromSeconds(5));

        var trace = Assert.Single(Read(store));
        Assert.Equal(MemoryTraceStore.MaxSpansPerTrace, trace.Spans.Count);
        Assert.Equal(6, trace.DroppedSpans);
    }

    [Fact]
    public void RequestsToIgnoredPaths_AreDroppedWithTheirSpans()
    {
        using var store = Create();
        store.IgnoredRequests.Add("/monitor");

        store.Add(Span("ui", "c", "r"), 10);
        store.Add(Span("ui", "r", null, kind: TraceSpanKind.Server, attributes: [new("url.path", "/monitor/api/GetTraces")]), 10);
        store.Add(Span("ui", "late", "r"), 10);
        store.Add(Span("app", "r", null, kind: TraceSpanKind.Server, attributes: [new("url.path", "/monitoring")]), 10);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["app"], Read(store).Select(t => t.TraceId));
    }

    [Fact]
    public void ReadBefore_PagesNewestFirstAcrossPools()
    {
        using var store = Create();
        for (var i = 0; i < 6; i++)
        {
            store.Add(Span($"t{i}", "r", null, status: i % 2 == 0 ? TraceSpanStatus.Error : TraceSpanStatus.Unset), 10);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        var first = store.ReadBefore(null, 4, TraceQueryMatcher.All, out var hasMore);
        Assert.Equal(["t5", "t4", "t3", "t2"], first.Select(t => t.TraceId));
        Assert.True(hasMore);

        var next = store.ReadBefore(first[^1].Id, 4, TraceQueryMatcher.All, out hasMore);
        Assert.Equal(["t1", "t0"], next.Select(t => t.TraceId));
        Assert.False(hasMore);

        var errors = store.ReadBefore(null, 10, TraceQueryMatcher.Create(new TraceQuery("status:error")), out _);
        Assert.Equal(["t4", "t2", "t0"], errors.Select(t => t.TraceId));
    }

    [Fact]
    public void InvalidOptions_FailAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Create(o => o.Errors.KeepTraces(-1)));
        Assert.Throws<InvalidOperationException>(() => Create(o => o.MaxTotalBytes = 0));
    }

    private MemoryTraceStore Create(Action<TraceStoreOptions>? configure = null)
    {
        var options = new TraceStoreOptions();
        configure?.Invoke(options);
        return new MemoryTraceStore(options, _time);
    }

    private static IReadOnlyList<TraceEntry> Read(MemoryTraceStore store) =>
        store.ReadBefore(null, 100, TraceQueryMatcher.All, out _);

    internal static TraceSpan Span(
        string traceId, string spanId, string? parentId,
        TraceSpanStatus status = TraceSpanStatus.Unset, TimeSpan? duration = null, TraceSpanKind kind = TraceSpanKind.Internal,
        KeyValuePair<string, string?>[]? attributes = null, string name = "op", string? service = "shop",
        DateTimeOffset? start = null, SpanEvent[]? events = null) =>
        new(traceId, spanId, parentId, name, kind, start ?? new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
            duration ?? TimeSpan.FromMilliseconds(10), status, null, service, "test", attributes ?? [], events ?? [], []);
}
