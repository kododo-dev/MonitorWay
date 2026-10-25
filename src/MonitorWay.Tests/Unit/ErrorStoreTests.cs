using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class ErrorStoreTests
{
    private const string Stack = "System.TimeoutException: {0}\n   at Shop.Payments.Gateway.Charge() in Gateway.cs:line 4";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 30, 0, TimeSpan.Zero));

    [Fact]
    public void SameTypeAndPlace_IsOneGroupWithTheLatestMessage()
    {
        using var store = Create();
        store.Add(Report("PayU late", traceId: "t1"));
        _time.Advance(TimeSpan.FromMinutes(5));
        store.Add(Report("Stripe late", traceId: "t2", service: "billing"));

        var group = Assert.Single(Read(store));
        Assert.Equal(2, group.Count);
        Assert.Equal("Stripe late", group.Message);
        Assert.Equal(["billing", "shop"], group.Services);
        Assert.Equal(_time.GetUtcNow().AddMinutes(-5), group.FirstSeen);
        Assert.Equal(_time.GetUtcNow(), group.LastSeen);
        Assert.Equal(["t2", "t1"], store.Find(group.Fingerprint)!.Group.Occurrences.Select(o => o.TraceId));
    }

    [Fact]
    public void LogAndSpanOfOneTrace_AreOneOccurrence()
    {
        using var store = Create();
        store.Add(Report("late", traceId: "t1", logId: 7, spanId: "outer"));
        store.Add(Report("late", traceId: "t1", spanId: "call", fromSpan: true));
        store.Add(Report("late", traceId: "t2", spanId: "call", fromSpan: true));

        var group = store.Find(Assert.Single(Read(store)).Fingerprint)!.Group;
        Assert.Equal(2, group.Count);
        var merged = group.Occurrences.Single(o => o.TraceId == "t1");
        Assert.True(merged.FromSpan);
        Assert.Equal(7, merged.LogEntryId);
        Assert.Equal("call", merged.SpanId);
    }

    [Fact]
    public void TwoLogEntriesOfOneTrace_AreTwoOccurrences()
    {
        using var store = Create();
        store.Add(Report("late", traceId: "t1", logId: 1));
        store.Add(Report("late", traceId: "t1", logId: 2));

        Assert.Equal(2, Assert.Single(Read(store)).Count);
    }

    [Fact]
    public void Occurrences_AreLimitedButCounted()
    {
        using var store = Create(o => o.MaxOccurrences = 3);
        for (var i = 0; i < 5; i++)
            store.Add(Report($"m{i}"));

        var group = store.Find(Assert.Single(Read(store)).Fingerprint)!.Group;
        Assert.Equal(5, group.Count);
        Assert.Equal(["m4", "m3", "m2"], group.Occurrences.Select(o => o.Message));
    }

    [Fact]
    public void Groups_AreLimited_TheLeastRecentGoes()
    {
        using var store = Create(o => o.MaxGroups = 2);
        store.Add(Report("a", type: "A"));
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Add(Report("b", type: "B"));
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Add(Report("c", type: "C"));

        Assert.Equal(["C", "B"], Read(store).Select(g => g.Type));
    }

    [Fact]
    public void GroupsNotSeenForMaxAge_AreRemoved()
    {
        using var store = Create(o => o.MaxAge = TimeSpan.FromHours(1));
        store.Add(Report("old"));

        _time.Advance(TimeSpan.FromMinutes(61));

        Assert.Empty(Read(store));
    }

    [Fact]
    public void Trend_CountsPerHour()
    {
        using var store = Create();
        store.Add(Report("a"));
        store.Add(Report("b"));
        _time.Advance(TimeSpan.FromHours(2));
        store.Add(Report("c"));

        var trend = Assert.Single(store.Read(ErrorQueryMatcher.All, byCount: false, 10)).Trend;
        Assert.Equal(24, trend.Length);
        Assert.Equal([2, 0, 1], trend[^3..]);
        Assert.Equal(MemoryErrorStore.TrendHours, store.Find(Read(store)[0].Fingerprint)!.Trend.Length);
    }

    [Fact]
    public void Read_SortsByLastSeenOrCount_AndFilters()
    {
        using var store = Create();
        store.Add(Report("x", type: "Frequent", endpoint: "POST /orders"));
        store.Add(Report("x", type: "Frequent", endpoint: "POST /orders"));
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Add(Report("y", type: "Recent", endpoint: "GET /health", traceId: "abc123"));

        Assert.Equal(["Recent", "Frequent"], Read(store).Select(g => g.Type));
        Assert.Equal(["Frequent", "Recent"], store.Read(ErrorQueryMatcher.All, byCount: true, 10).Select(v => v.Group.Type));
        Assert.Equal(["Frequent"], Read(store, "endpoint:POST*").Select(g => g.Type));
        Assert.Equal(["Recent"], Read(store, "-type:Frequent").Select(g => g.Type));
        Assert.Equal(["Recent"], Read(store, "trace:abc").Select(g => g.Type));
        Assert.Throws<ArgumentException>(() => ErrorQueryMatcher.Create(new ErrorQuery("color:red")));
    }

    private MemoryErrorStore Create(Action<ErrorStoreOptions>? configure = null)
    {
        var options = new ErrorStoreOptions();
        configure?.Invoke(options);
        return new MemoryErrorStore(options, _time);
    }

    private ReportedException Report(
        string message, string type = "System.TimeoutException", string? traceId = null, string? spanId = null,
        long? logId = null, bool fromSpan = false, string service = "shop", string? endpoint = "POST /orders") =>
        new(_time.GetUtcNow(), type, message, string.Format(Stack, message).Replace("System.TimeoutException", type),
            service, endpoint, traceId, spanId, logId, logId is null ? null : "Shop.Payments", fromSpan);

    private static IReadOnlyList<ErrorGroup> Read(MemoryErrorStore store, string? text = null) =>
        store.Read(text is null ? ErrorQueryMatcher.All : ErrorQueryMatcher.Create(new ErrorQuery(text)), byCount: false, 100)
            .Select(v => v.Group).ToArray();
}
