using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class FileLogSourceTests
{
    private static readonly LogQueryMatcher All = LogQueryMatcher.All;
    private static readonly LogQueryMatcher ErrorsOnly = LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Error]));

    private static FileLogSource Source(ClefFile file, bool indexed)
    {
        var source = new FileLogSource(file.Path, "app.clef");
        if (indexed)
            source.UpdateIndex(CancellationToken.None);
        return source;
    }

    private static int OrderId(LogEntry entry) => int.Parse(entry.Properties.Single(p => p.Key == "OrderId").Value!);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadBefore_ReturnsNewestEntriesInOrder(bool indexed)
    {
        using var file = new ClefFile().AppendMany(100);
        var source = Source(file, indexed);

        var result = source.ReadBefore(null, 3, All, CancellationToken.None);

        Assert.Equal([97, 98, 99], result.Entries.Select(OrderId));
        Assert.True(result.HasMore);
        Assert.Equal("Order 99 handled", result.Entries[^1].Message);
        Assert.Equal(ClefFile.TimeOf(99), result.Entries[^1].Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagingBackward_VisitsEveryEntryOnce(bool indexed)
    {
        using var file = new ClefFile().AppendMany(5_000);
        var source = Source(file, indexed);

        var seen = new List<int>();
        long? before = null;
        while (true)
        {
            var page = source.ReadBefore(before, 333, All, CancellationToken.None);
            seen.InsertRange(0, page.Entries.Select(OrderId));
            if (!page.HasMore)
                break;
            before = page.Cursor;
        }

        Assert.Equal(Enumerable.Range(0, 5_000), seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagingForward_VisitsEveryEntryOnce(bool indexed)
    {
        using var file = new ClefFile().AppendMany(5_000);
        var source = Source(file, indexed);

        var seen = new List<int>();
        long after = 0;
        while (true)
        {
            var page = source.ReadAfter(after, 333, All, CancellationToken.None);
            seen.AddRange(page.Entries.Select(OrderId));
            after = page.Cursor!.Value;
            if (!page.HasMore)
                break;
        }

        Assert.Equal(Enumerable.Range(0, 5_000), seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LevelFilter_ReturnsOnlyMatchingEntries(bool indexed)
    {
        using var file = new ClefFile().AppendMany(5_000, errorEvery: 1_000);
        var source = Source(file, indexed);

        var result = source.ReadBefore(null, 100, ErrorsOnly, CancellationToken.None);

        Assert.Equal([0, 1_000, 2_000, 3_000, 4_000], result.Entries.Select(OrderId));
        Assert.All(result.Entries, e => Assert.Equal(LogLevel.Error, e.Level));
    }

    [Fact]
    public void Index_SkipsChunksWithoutMatchingLevels()
    {
        using var file = new ClefFile().AppendMany(20_000);
        file.Append(ClefFile.Line(20_000, LogLevel.Error));
        var source = Source(file, indexed: true);
        source.MaxScanBytes = FileIndex.TargetChunkSize; // Without skipping this would stop after one chunk.

        var result = source.ReadAfter(0, 10, ErrorsOnly, CancellationToken.None);

        Assert.True(source.Index.ChunkCount > 10);
        Assert.Equal([20_000], result.Entries.Select(OrderId));
        Assert.False(result.HasMore);
    }

    [Fact]
    public void Index_SkipsChunksOutsideTimeRange()
    {
        using var file = new ClefFile().AppendMany(20_000);
        var source = Source(file, indexed: true);
        source.MaxScanBytes = FileIndex.TargetChunkSize * 2;
        var matcher = LogQueryMatcher.Create(new LogQuery(From: ClefFile.TimeOf(10), To: ClefFile.TimeOf(12)));

        var result = source.ReadBefore(null, 10, matcher, CancellationToken.None);

        Assert.Equal([10, 11, 12], result.Entries.Select(OrderId));
    }

    [Fact]
    public void TextSearch_InParallelSegments_FindsTheSameAsAFullScan()
    {
        using var file = new ClefFile().AppendMany(200_000, errorEvery: 3);
        var source = Source(file, indexed: true);
        // Matches orders whose number contains "77", among errors only, so segments and skipped chunks both occur.
        var matcher = LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Error], Text: "77"));
        var expected = Enumerable.Range(0, 200_000).Where(i => i % 3 == 0 && i.ToString().Contains("77")).ToList();

        var backward = new List<int>();
        long? before = null;
        while (true)
        {
            var page = source.ReadBefore(before, 997, matcher, CancellationToken.None);
            backward.InsertRange(0, page.Entries.Select(OrderId));
            if (!page.HasMore)
                break;
            before = page.Cursor;
        }

        var forward = new List<int>();
        long after = 0;
        while (true)
        {
            var page = source.ReadAfter(after, 997, matcher, CancellationToken.None);
            forward.AddRange(page.Entries.Select(OrderId));
            after = page.Cursor!.Value;
            if (!page.HasMore)
                break;
        }

        Assert.True(new FileInfo(file.Path).Length > 16 * 1024 * 1024);
        Assert.Equal(expected, backward);
        Assert.Equal(expected, forward);
    }

    [Fact]
    public void ScanBudget_ReturnsCursorToContinueFrom()
    {
        using var file = new ClefFile().AppendMany(20_000);
        var lines = File.ReadAllLines(file.Path);
        lines[100] = ClefFile.Line(100, message: "needle");
        File.WriteAllLines(file.Path, lines);
        var source = Source(file, indexed: false);
        source.MaxScanBytes = 100_000;
        var matcher = LogQueryMatcher.Create(new LogQuery(Text: "needle"));

        var calls = 0;
        long? before = null;
        ReadResult page;
        do
        {
            page = source.ReadBefore(before, 10, matcher, CancellationToken.None);
            before = page.Cursor;
            calls++;
        } while (page.Entries.Count == 0 && page.HasMore);

        Assert.True(calls > 1);
        Assert.Equal("needle", Assert.Single(page.Entries).Message);
    }

    [Fact]
    public void PartialLastLine_IsIgnoredUntilComplete()
    {
        using var file = new ClefFile().AppendMany(3);
        var line = ClefFile.Line(3);
        file.AppendRaw(line[..20]);
        var source = Source(file, indexed: true);

        Assert.Equal([0, 1, 2], source.ReadBefore(null, 10, All, CancellationToken.None).Entries.Select(OrderId));

        file.AppendRaw(line[20..] + "\n");
        source.UpdateIndex(CancellationToken.None);

        Assert.Equal([0, 1, 2, 3], source.ReadBefore(null, 10, All, CancellationToken.None).Entries.Select(OrderId));
    }

    [Fact]
    public void Append_IsVisibleFromLastCursor()
    {
        using var file = new ClefFile().AppendMany(10);
        var source = Source(file, indexed: true);
        var cursor = source.ReadAfter(long.MaxValue, 1, All, CancellationToken.None).Cursor!.Value;

        file.AppendMany(3, from: 10);
        source.UpdateIndex(CancellationToken.None);
        var result = source.ReadAfter(cursor, 10, All, CancellationToken.None);

        Assert.Equal([10, 11, 12], result.Entries.Select(OrderId));
        Assert.Equal(13, source.GetStats().LevelCounts[(int)LogLevel.Information]);
    }

    [Fact]
    public void Truncation_ResetsIndexAndChangesVersion()
    {
        using var file = new ClefFile().AppendMany(1_000);
        var source = Source(file, indexed: true);
        var version = source.Version;

        File.WriteAllText(file.Path, ClefFile.Line(5_000, LogLevel.Warning) + "\n");
        source.UpdateIndex(CancellationToken.None);

        Assert.NotEqual(version, source.Version);
        Assert.Equal([5_000], source.ReadBefore(null, 10, All, CancellationToken.None).Entries.Select(OrderId));
        Assert.Equal(1, source.GetStats().LevelCounts[(int)LogLevel.Warning]);
    }

    [Fact]
    public void Replacement_WithLongerFile_IsDetected()
    {
        using var file = new ClefFile().AppendMany(10);
        var source = Source(file, indexed: true);
        var version = source.Version;

        var replaced = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++)
            replaced.Append(ClefFile.Line(10_000 + i)).Append('\n');
        File.WriteAllText(file.Path, replaced.ToString());
        source.UpdateIndex(CancellationToken.None);

        Assert.NotEqual(version, source.Version);
        Assert.Equal(50, source.GetStats().LevelCounts[(int)LogLevel.Information]);
    }

    [Fact]
    public void Find_ReturnsEntryByIdAndRejectsOthers()
    {
        using var file = new ClefFile().AppendMany(5);
        var source = Source(file, indexed: false);
        var third = source.ReadAfter(0, 5, All, CancellationToken.None).Entries[2];

        Assert.Equal(2, OrderId(source.Find(third.Id)!));
        Assert.Null(source.Find(third.Id + 3));
        Assert.Null(source.Find(10_000_000));
    }

    [Fact]
    public void WindowsLineEndingsAndBlankLines_AreHandled()
    {
        using var file = new ClefFile();
        file.AppendRaw(ClefFile.Line(0) + "\r\n\r\n" + ClefFile.Line(1) + "\r\n");
        var source = Source(file, indexed: true);

        Assert.Equal([0, 1], source.ReadBefore(null, 10, All, CancellationToken.None).Entries.Select(OrderId));
        Assert.Equal([0, 1], source.ReadAfter(0, 10, All, CancellationToken.None).Entries.Select(OrderId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinesLongerThanBlocks_AreRead(bool indexed)
    {
        using var file = new ClefFile().AppendMany(2);
        file.Append(ClefFile.Line(2, LogLevel.Error, exception: new string('x', 300_000)));
        file.AppendMany(2, from: 3);
        var source = Source(file, indexed);

        var entries = source.ReadBefore(null, 10, All, CancellationToken.None).Entries;
        Assert.Equal([0, 1, 2, 3, 4], entries.Select(OrderId));
        Assert.Equal(300_000, entries[2].Exception!.Length);
        Assert.Equal([0, 1, 2, 3, 4], source.ReadAfter(0, 10, All, CancellationToken.None).Entries.Select(OrderId));
    }

    [Fact]
    public void BinaryFile_ReportsError()
    {
        using var file = new ClefFile("app.bin");
        File.WriteAllBytes(file.Path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x0A, 0x0A]);
        var source = Source(file, indexed: false);

        Assert.Empty(source.ReadBefore(null, 10, All, CancellationToken.None).Entries);
        Assert.NotNull(source.Error);
    }

    [Fact]
    public void Index_CountsLevels()
    {
        using var file = new ClefFile().AppendMany(10_000, errorEvery: 10);
        var source = Source(file, indexed: true);

        var stats = source.GetStats();
        Assert.Equal(1_000, stats.LevelCounts[(int)LogLevel.Error]);
        Assert.Equal(9_000, stats.LevelCounts[(int)LogLevel.Information]);
        Assert.Equal(stats.Length, stats.IndexedEnd);
    }
}
