using System.Text;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

/// <summary>Text logs whose entries span several lines, read with the Serilog default template.</summary>
public class MultiLineTests
{
    private static readonly LogQueryMatcher All = LogQueryMatcher.All;
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Entry i has i % 4 stack frames; every 4th entry is an error with an exception.</summary>
    private static string Entry(int i, int frames = -1)
    {
        frames = frames < 0 ? i % 4 : frames;
        var text = new StringBuilder();
        var level = frames > 0 ? "ERR" : "INF";
        text.Append($"{Start.AddSeconds(i):yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] Order {i} handled\n");
        if (frames > 0)
        {
            text.Append($"System.InvalidOperationException: order {i}\n");
            for (var f = 0; f < frames; f++)
                text.Append($"   at Shop.Orders.Step{f}()\n");
        }
        return text.ToString();
    }

    private static ClefFile TextLog(int count)
    {
        var file = new ClefFile("app.log");
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
            text.Append(Entry(i));
        file.AppendRaw(text.ToString());
        return file;
    }

    private static FileLogSource Source(ClefFile file, bool indexed)
    {
        // Not about the scan budget: on a busy machine (all frameworks tested at once) 300 ms may end a read early.
        var source = new FileLogSource(file.Path, "app.log") { MaxScanTime = TimeSpan.FromMinutes(1) };
        if (indexed)
            source.UpdateIndex(CancellationToken.None);
        return source;
    }

    private static int Order(LogEntry entry) => int.Parse(entry.Message.Split(' ')[1]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagingBackward_KeepsStackTracesWithTheirEntries(bool indexed)
    {
        using var file = TextLog(3_000);
        var source = Source(file, indexed);
        Assert.Equal("serilog", source.Format?.Name ?? DetectViaRead(source));

        var seen = new List<LogEntry>();
        long? before = null;
        while (true)
        {
            var page = source.ReadBefore(before, 97, All, CancellationToken.None);
            seen.InsertRange(0, page.Entries);
            if (!page.HasMore)
                break;
            before = page.Cursor;
        }

        Assert.Equal(Enumerable.Range(0, 3_000), seen.Select(Order));
        Assert.All(seen, e => Assert.Equal(Order(e) % 4, e.Exception?.Split('\n').Length - 1 ?? 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagingForward_KeepsStackTracesWithTheirEntries(bool indexed)
    {
        using var file = TextLog(3_000);
        var source = Source(file, indexed);

        var seen = new List<LogEntry>();
        long after = 0;
        while (true)
        {
            var page = source.ReadAfter(after, 97, All, CancellationToken.None);
            seen.AddRange(page.Entries);
            after = page.Cursor!.Value;
            if (!page.HasMore)
                break;
        }

        Assert.Equal(Enumerable.Range(0, 3_000), seen.Select(Order));
        Assert.All(seen, e => Assert.Equal(Order(e) % 4, e.Exception?.Split('\n').Length - 1 ?? 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StackTraceLongerThanBlocks_IsOneEntry(bool indexed)
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw(Entry(0, 0) + Entry(1, 5_000) + Entry(2, 0) + Entry(3, 3_000) + Entry(4, 0));
        var source = Source(file, indexed);

        var newest = source.ReadBefore(null, 10, All, CancellationToken.None).Entries;
        var oldest = source.ReadAfter(0, 10, All, CancellationToken.None).Entries;

        Assert.Equal([0, 1, 2, 3, 4], newest.Select(Order));
        Assert.Equal([0, 1, 2, 3, 4], oldest.Select(Order));
        Assert.Equal(5_001, newest[1].Exception!.Split('\n').Length);
        Assert.Equal(3_001, source.Find(newest[3].Id)!.Exception!.Split('\n').Length);
    }

    [Fact]
    public void LevelCountsAndChunkSkipping_UseEntriesNotLines()
    {
        using var file = TextLog(4_000);
        var source = Source(file, indexed: true);

        var stats = source.GetStats();
        Assert.Equal(1_000, stats.LevelCounts[(int)LogLevel.Information]);
        Assert.Equal(3_000, stats.LevelCounts[(int)LogLevel.Error]);

        var infos = source.ReadAfter(0, 2_000, LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Information])), CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 1_000).Select(i => i * 4), infos.Entries.Select(Order));
    }

    [Fact]
    public void ContinuationWrittenAfterIndexing_JoinsItsEntry()
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw(Entry(0, 0) + $"{Start.AddSeconds(1):yyyy-MM-dd HH:mm:ss.fff zzz} [ERR] Order 1 handled\nSystem.Exception: late\n");
        var source = Source(file, indexed: true);

        file.AppendRaw("   at Shop.Late()\n   at Shop.Later()\n");
        source.UpdateIndex(CancellationToken.None);
        file.AppendRaw(Entry(2, 0));

        var entries = source.ReadBefore(null, 10, All, CancellationToken.None).Entries;
        Assert.Equal([0, 1, 2], entries.Select(Order));
        Assert.Equal("System.Exception: late\n   at Shop.Late()\n   at Shop.Later()", entries[1].Exception);

        var forward = source.ReadAfter(0, 10, All, CancellationToken.None).Entries;
        Assert.Equal([0, 1, 2], forward.Select(Order));
    }

    [Fact]
    public void FileStartingWithContinuationLines_ShowsThemAsAnEntry()
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw("2026-10-13 08:00:00.000 +02:00 [INF] Order 0 handled\n");
        // Simulate a file cut in the middle of an entry by a copy tool: the first lines continue an older entry.
        File.WriteAllText(file.Path, "   at Shop.Cut()\n" + Entry(1, 0) + Entry(2, 0));
        var source = Source(file, indexed: true);

        var entries = source.ReadAfter(0, 10, All, CancellationToken.None).Entries;

        Assert.Equal(3, entries.Count);
        Assert.Contains("Shop.Cut", entries[0].Message);
    }

    [Fact]
    public void ByteOrderMark_IsNotPartOfTheFirstEntry()
    {
        using var file = new ClefFile("app.log");
        File.WriteAllText(file.Path, Entry(0, 0) + Entry(1, 0), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var source = Source(file, indexed: true);

        var entries = source.ReadAfter(0, 10, All, CancellationToken.None).Entries;

        Assert.Equal([0, 1], entries.Select(Order));
        Assert.Equal(LogLevel.Information, entries[0].Level);
        Assert.NotNull(entries[0].Timestamp);
    }

    [Fact]
    public void ConfiguredTemplate_IsUsed()
    {
        using var file = new ClefFile("custom.log");
        file.AppendRaw("09:28:10 WRN Shop.Orders | Slow\n09:28:11 ERR Shop.Payments | Failed\nSystem.Exception: x\n");
        var options = new FileSourceOptions { Pattern = "*" }.UseTemplate("{Timestamp:HH:mm:ss} {Level:u3} {SourceContext} | {Message}{NewLine}{Exception}");
        var source = new FileLogSource(file.Path, "custom.log", options);

        var entries = source.ReadAfter(0, 10, All, CancellationToken.None).Entries;

        Assert.Equal(["Shop.Orders", "Shop.Payments"], entries.Select(e => e.Category));
        Assert.Equal("System.Exception: x", entries[1].Exception);
        Assert.Equal("template", source.Format!.Name);
    }

    private static string DetectViaRead(FileLogSource source)
    {
        source.ReadBefore(null, 1, All, CancellationToken.None);
        return source.Format!.Name;
    }
}
