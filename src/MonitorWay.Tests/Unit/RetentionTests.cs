using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class RetentionTests
{
    [Fact]
    public void DefaultPools_KeepErrorsUnderDebugFlood()
    {
        using var buffer = TestBuffer.Create();

        buffer.Append(LogLevel.Error, 1);
        buffer.Append(LogLevel.Debug, 60_000);

        Assert.Equal(1, buffer.CountOf(LogLevel.Error));
        Assert.Equal(50_000, buffer.CountOf(LogLevel.Debug));
    }

    [Fact]
    public void MaxEntries_RemovesOldestEntriesOfThePool()
    {
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Information).KeepEntries(3));

        buffer.Append(LogLevel.Information, 5);

        var entries = buffer.ReadBefore(null, 10, LogQueryMatcher.All, out _);
        Assert.Equal(["Information 2", "Information 3", "Information 4"], entries.Select(e => e.Message));
    }

    [Fact]
    public void MaxBytes_RemovesOldestEntriesOfThePool()
    {
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Information).KeepBytes(250));

        buffer.Append(LogLevel.Information, 5, size: 100);

        Assert.Equal(2, buffer.CountOf(LogLevel.Information));
    }

    [Fact]
    public void Pools_HaveIndependentLimits()
    {
        using var buffer = TestBuffer.Create(m =>
        {
            m.Pool(LogLevel.Error).KeepEntries(10);
            m.Pool(LogLevel.Debug).KeepEntries(2);
        });

        buffer.Append(LogLevel.Error, 5);
        buffer.Append(LogLevel.Debug, 5);

        Assert.Equal(5, buffer.CountOf(LogLevel.Error));
        Assert.Equal(2, buffer.CountOf(LogLevel.Debug));
    }

    [Fact]
    public void MaxAge_RemovesExpiredEntriesOnTimer()
    {
        var time = new FakeTimeProvider();
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Warning).KeepFor(TimeSpan.FromMinutes(1)), time);

        buffer.Append(LogLevel.Warning, 3, time: time);
        time.Advance(TimeSpan.FromSeconds(30));
        buffer.Append(LogLevel.Warning, 2, time: time);
        time.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(2, buffer.CountOf(LogLevel.Warning));
    }

    [Fact]
    public void MaxAge_EntryOlderThanTheLimitIsNotKept()
    {
        var time = new FakeTimeProvider();
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Warning).KeepFor(TimeSpan.FromMinutes(1)), time);

        buffer.Append(TestBuffer.Entry(LogLevel.Warning, "old", timestamp: time.GetUtcNow() - TimeSpan.FromMinutes(5)));
        buffer.Append(TestBuffer.Entry(LogLevel.Information, "info", timestamp: time.GetUtcNow() - TimeSpan.FromMinutes(5)));

        var entries = buffer.ReadBefore(null, 10, LogQueryMatcher.All, out _);

        Assert.Equal(["info"], entries.Select(e => e.Message));
    }

    [Fact]
    public void GlobalLimit_RemovesLowestLevelsFirst()
    {
        using var buffer = TestBuffer.Create(m =>
        {
            m.MaxTotalBytes = 1_000;
            m.Pool(LogLevel.Error);
            m.DefaultPool.MaxEntries = null;
        });

        buffer.Append(LogLevel.Error, 5, size: 100);
        buffer.Append(LogLevel.Warning, 2, size: 100);
        buffer.Append(LogLevel.Debug, 8, size: 100);

        Assert.Equal(5, buffer.CountOf(LogLevel.Error));
        Assert.True(buffer.GetStats().TotalBytes <= 1_000);
        // Debug and Warning share the default pool, so its oldest entries (the warnings) go first.
        Assert.Equal(0, buffer.CountOf(LogLevel.Warning));
        Assert.Equal(5, buffer.CountOf(LogLevel.Debug));
    }

    [Fact]
    public void GlobalLimit_RemovesErrorsWhenNothingElseIsLeft()
    {
        using var buffer = TestBuffer.Create(m =>
        {
            m.MaxTotalBytes = 300;
            m.Pool(LogLevel.Error);
        });

        buffer.Append(LogLevel.Error, 5, size: 100);

        Assert.Equal(3, buffer.CountOf(LogLevel.Error));
    }

    [Fact]
    public void UnassignedLevels_GoToDefaultPool()
    {
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Error));

        var pools = buffer.GetStats().Pools;

        Assert.Equal(2, pools.Count);
        Assert.Equal([LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Critical], pools[0].Levels);
        Assert.Equal(50_000, pools[0].MaxEntries);
        Assert.Equal([LogLevel.Error], pools[1].Levels);
    }

    [Fact]
    public void Pools_AreOrderedByLowestLevel()
    {
        using var buffer = TestBuffer.Create(m =>
        {
            m.Pool(LogLevel.Critical);
            m.Pool(LogLevel.Information, LogLevel.Trace);
        });

        Assert.Equal(
            [LogLevel.Trace, LogLevel.Debug, LogLevel.Critical],
            buffer.GetStats().Pools.Select(p => p.Levels[0]));
    }

    [Fact]
    public void LevelInTwoPools_Throws()
    {
        var options = new MemoryBufferOptions();
        options.Pool(LogLevel.Error);
        options.Pool(LogLevel.Error, LogLevel.Critical);

        var error = Assert.Throws<InvalidOperationException>(() => MemoryLogBuffer.Validate(options));
        Assert.Contains("Error", error.Message);
    }

    [Fact]
    public void PoolWithoutLevels_Throws()
    {
        var options = new MemoryBufferOptions();
        options.Pool();

        Assert.Throws<InvalidOperationException>(() => MemoryLogBuffer.Validate(options));
    }

    [Fact]
    public void NonPositiveLimit_Throws()
    {
        var options = new MemoryBufferOptions();
        options.Pool(LogLevel.Error).KeepFor(TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => MemoryLogBuffer.Validate(options));
    }

    [Fact]
    public void Stats_ReportEvictedEntries()
    {
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Information).KeepEntries(2));

        buffer.Append(LogLevel.Information, 5);

        var pool = buffer.GetStats().Pools.Single(p => p.Levels.Contains(LogLevel.Information));
        Assert.Equal(3, pool.Evicted);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Pool_ReleasesSegmentsAfterBurst()
    {
        using var buffer = TestBuffer.Create(m => m.Pool(LogLevel.Information).KeepEntries(10));

        buffer.Append(LogLevel.Information, 10_000);

        var entries = buffer.ReadBefore(null, 100, LogQueryMatcher.All, out var hasMore);
        Assert.Equal(10, entries.Count);
        Assert.False(hasMore);
        Assert.Equal("Information 9999", entries[^1].Message);
    }
}
