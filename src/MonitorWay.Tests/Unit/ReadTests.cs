using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class ReadTests
{
    [Fact]
    public void ReadBefore_MergesPoolsInIdOrder()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "a"));
        buffer.Append(TestBuffer.Entry(LogLevel.Error, "b"));
        buffer.Append(TestBuffer.Entry(LogLevel.Debug, "c"));
        buffer.Append(TestBuffer.Entry(LogLevel.Critical, "d"));

        var entries = buffer.ReadBefore(null, 10, LogQueryMatcher.All, out var hasMore);

        Assert.Equal(["a", "b", "c", "d"], entries.Select(e => e.Message));
        Assert.Equal([1L, 2, 3, 4], entries.Select(e => e.Id));
        Assert.False(hasMore);
    }

    [Fact]
    public void ReadBefore_ReturnsNewestPageInAscendingOrder()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(LogLevel.Information, 10);

        var entries = buffer.ReadBefore(null, 3, LogQueryMatcher.All, out var hasMore);

        Assert.Equal([8L, 9, 10], entries.Select(e => e.Id));
        Assert.True(hasMore);
    }

    [Fact]
    public void ReadBefore_ContinuesFromCursor()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(LogLevel.Information, 10);

        var entries = buffer.ReadBefore(8, 3, LogQueryMatcher.All, out var hasMore);

        Assert.Equal([5L, 6, 7], entries.Select(e => e.Id));
        Assert.True(hasMore);
    }

    [Fact]
    public void ReadAfter_ReturnsOldestFirst()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(LogLevel.Information, 10);

        var entries = buffer.ReadAfter(7, 2, LogQueryMatcher.All, out var hasMore);

        Assert.Equal([8L, 9], entries.Select(e => e.Id));
        Assert.True(hasMore);
    }

    [Fact]
    public void Reads_SpanManyBatchesAcrossPools()
    {
        using var buffer = TestBuffer.Create(m => m.DefaultPool.MaxEntries = null);
        for (var i = 0; i < 3_000; i++)
            buffer.Append(TestBuffer.Entry(i % 3 == 0 ? LogLevel.Error : LogLevel.Debug, i.ToString()));

        var newest = buffer.ReadBefore(null, 2_500, LogQueryMatcher.All, out _);
        var oldest = buffer.ReadAfter(0, 2_500, LogQueryMatcher.All, out _);

        Assert.Equal(Enumerable.Range(501, 2_500).Select(i => (long)i), newest.Select(e => e.Id));
        Assert.Equal(Enumerable.Range(1, 2_500).Select(i => (long)i), oldest.Select(e => e.Id));
    }

    [Fact]
    public void Filter_ByLevel()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(LogLevel.Information, 5);
        buffer.Append(LogLevel.Error, 2);
        buffer.Append(LogLevel.Warning, 3);

        var matcher = LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Error, LogLevel.Warning]));
        var entries = buffer.ReadBefore(null, 100, matcher, out _);

        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.True(e.Level is LogLevel.Error or LogLevel.Warning));
    }

    [Fact]
    public void Filter_ByTextIgnoresCaseByDefault()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "Order 42 placed"));
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "Payment received"));

        var entries = buffer.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Text: "ORDER")), out _);

        Assert.Equal(["Order 42 placed"], entries.Select(e => e.Message));
    }

    [Fact]
    public void Filter_CaseSensitive()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "Order 42 placed"));

        var entries = buffer.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Text: "ORDER", CaseSensitive: true)), out _);

        Assert.Empty(entries);
    }

    [Fact]
    public void Filter_ByRegex()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "Order 42 placed"));
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "Order abc placed"));

        var entries = buffer.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Text: @"order \d+", Regex: true)), out _);

        Assert.Equal(["Order 42 placed"], entries.Select(e => e.Message));
    }

    [Fact]
    public void Filter_SearchesPropertiesAndException()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "first") with { Properties = [new("CustomerId", "c-77")] });
        buffer.Append(TestBuffer.Entry(LogLevel.Error, "second") with { Exception = "System.TimeoutException: boom" });
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "third"));

        Assert.Equal(["first"], buffer.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Text: "c-77")), out _).Select(e => e.Message));
        Assert.Equal(["second"], buffer.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Text: "Timeout")), out _).Select(e => e.Message));
    }

    [Fact]
    public void Filter_InvalidRegexThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() => LogQueryMatcher.Create(new LogQuery(Text: "(", Regex: true)));
    }

    [Fact]
    public void Find_ReturnsEntryById()
    {
        using var buffer = TestBuffer.Create();
        buffer.Append(LogLevel.Information, 3);
        buffer.Append(LogLevel.Error, 1);

        Assert.Equal("Error 0", buffer.Find(4)?.Message);
        Assert.Null(buffer.Find(99));
    }

    [Fact]
    public void Subscription_ReceivesNewEntriesAndCountsDropped()
    {
        using var buffer = TestBuffer.Create();
        using var subscription = buffer.Subscribe(capacity: 2);

        buffer.Append(LogLevel.Information, 5);

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal(1, first.Id);
        Assert.Equal(3, subscription.TakeDropped());
        Assert.Equal(0, subscription.TakeDropped());
    }
}
