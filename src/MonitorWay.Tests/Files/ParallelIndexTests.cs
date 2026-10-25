using System.Text;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

/// <summary>Files above the parallel threshold (32 MB) are indexed in segments on several threads.</summary>
public class ParallelIndexTests
{
    private const long TargetSize = 40L * 1024 * 1024;

    [Fact]
    public void Clef_SegmentsJoinIntoOneConsistentIndex()
    {
        using var file = new ClefFile();
        var errors = 0;
        var entries = 0;
        var text = new StringBuilder();
        while (new FileInfo(file.Path).Length < TargetSize)
        {
            text.Clear();
            for (var i = 0; i < 10_000; i++, entries++)
            {
                var error = entries % 7 == 0;
                errors += error ? 1 : 0;
                text.Append(ClefFile.Line(entries, error ? LogLevel.Error : LogLevel.Information)).Append('\n');
            }
            file.AppendRaw(text.ToString());
        }

        var source = new FileLogSource(file.Path, "big.clef");
        source.UpdateIndex(CancellationToken.None);

        AssertConsistent(source, entries);
        var counts = source.GetStats().LevelCounts;
        Assert.Equal(errors, counts[(int)LogLevel.Error]);
        Assert.Equal(entries - errors, counts[(int)LogLevel.Information]);
        Assert.Equal(entries, Assert.Single(source.GetCategoryCounts(), c => c.Key == "Shop.Orders").Value);

        var errorPage = source.ReadAfter(0, 3, LogQueryMatcher.Create(new Core.Model.LogQuery(Levels: [LogLevel.Error])), CancellationToken.None);
        Assert.Equal(["Order 0 handled", "Order 7 handled", "Order 14 handled"], errorPage.Entries.Select(e => e.Message));
    }

    [Fact]
    public void Text_SegmentBoundariesFallOnEntryStarts()
    {
        using var file = new ClefFile("big.log");
        var start = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(2));
        var entries = 0;
        var text = new StringBuilder();
        while (new FileInfo(file.Path).Length < TargetSize)
        {
            text.Clear();
            for (var i = 0; i < 5_000; i++, entries++)
            {
                text.Append($"{start.AddMilliseconds(entries):yyyy-MM-dd HH:mm:ss.fff zzz} [ERR] Order {entries} failed\n");
                // Long stack traces make it likely that a segment's nominal split point falls inside one.
                text.Append("System.Exception: boom\n");
                for (var frame = 0; frame < entries % 30; frame++)
                    text.Append($"   at Shop.Frame{frame}()\n");
            }
            file.AppendRaw(text.ToString());
        }

        var source = new FileLogSource(file.Path, "big.log");
        source.UpdateIndex(CancellationToken.None);

        AssertConsistent(source, entries);
        Assert.Equal(entries, source.GetStats().LevelCounts[(int)LogLevel.Error]);

        // An entry from the middle of the file still has its whole stack trace.
        var middle = source.ReadAfter(new FileInfo(file.Path).Length / 2, 1, LogQueryMatcher.All, CancellationToken.None).Entries.Single();
        var order = int.Parse(middle.Message.Split(' ')[1]);
        Assert.Equal(order % 30 + 1, middle.Exception!.Split('\n').Length);
    }

    private static void AssertConsistent(FileLogSource source, int entries)
    {
        var index = source.Index;
        Assert.Equal(new FileInfo(source.Path).Length, index.IndexedEnd);
        Assert.True(index.ChunkCount > 100);

        long end = 0, nextEntry = 0;
        for (var i = 0; i < index.ChunkCount; i++)
        {
            Assert.True(index.TryGetChunk(end, out var chunk));
            Assert.Equal(end, chunk.Offset);
            Assert.Equal(nextEntry, chunk.FirstEntry);
            end = chunk.End;
            nextEntry += chunk.EntryCount;
        }
        Assert.Equal(index.IndexedEnd, end);
        Assert.Equal(entries, nextEntry);
    }
}
