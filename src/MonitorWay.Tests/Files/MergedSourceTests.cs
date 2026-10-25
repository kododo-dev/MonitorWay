using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class MergedSourceTests
{
    private static FileLogSource Source(ClefFile file, bool indexed = true, string? path = null)
    {
        var source = new FileLogSource(path ?? file.Path, System.IO.Path.GetFileName(path ?? file.Path));
        if (indexed)
            source.UpdateIndex(CancellationToken.None);
        return source;
    }

    private static int OrderId(LogEntry entry) => int.Parse(entry.Properties.Single(p => p.Key == "OrderId").Value!);

    /// <summary>Two files written at the same time: even orders in one, odd in the other.</summary>
    private static (ClefFile Even, ClefFile Odd) Interleaved(int count)
    {
        var even = new ClefFile();
        var odd = new ClefFile();
        even.Append(Enumerable.Range(0, count).Where(i => i % 2 == 0).Select(i => ClefFile.Line(i)).ToArray());
        odd.Append(Enumerable.Range(0, count).Where(i => i % 2 == 1).Select(i => ClefFile.Line(i)).ToArray());
        return (even, odd);
    }

    private static List<int> ReadAllBackward(ILogSource source, int pageSize, LogQueryMatcher matcher)
    {
        var all = new List<int>();
        long? before = null;
        for (var pages = 0; pages < 10_000; pages++)
        {
            var page = source.ReadBefore(before, pageSize, matcher, CancellationToken.None);
            all.InsertRange(0, page.Entries.Select(OrderId));
            if (!page.HasMore)
                return all;
            before = page.Cursor ?? page.Entries[0].Id;
        }
        throw new InvalidOperationException("Reading did not finish.");
    }

    private static List<int> ReadAllForward(ILogSource source, int pageSize, LogQueryMatcher matcher)
    {
        var all = new List<int>();
        long after = 0;
        for (var pages = 0; pages < 10_000; pages++)
        {
            var page = source.ReadAfter(after, pageSize, matcher, CancellationToken.None);
            all.AddRange(page.Entries.Select(OrderId));
            if (!page.HasMore)
                return all;
            after = page.Cursor ?? page.Entries[^1].Id;
        }
        throw new InvalidOperationException("Reading did not finish.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Entries_AreInTimeOrderAcrossMembers_PagingBothWays(bool indexed)
    {
        var (even, odd) = Interleaved(101);
        using var _ = even;
        using var __ = odd;
        var merged = new MergedSource("m", "m", null, [Source(even, indexed), Source(odd, indexed)]);

        Assert.Equal(Enumerable.Range(0, 101), ReadAllBackward(merged, 7, LogQueryMatcher.All));
        Assert.Equal(Enumerable.Range(0, 101), ReadAllForward(merged, 7, LogQueryMatcher.All));
    }

    [Fact]
    public void RotatedFiles_ReadAsOneLog()
    {
        using var older = new ClefFile().AppendMany(30);
        using var current = new ClefFile().AppendMany(20, from: 30);
        var merged = new MergedSource("m", "m", null, [Source(current), Source(older)]);

        var newest = merged.ReadBefore(null, 5, LogQueryMatcher.All, CancellationToken.None);

        Assert.Equal([45, 46, 47, 48, 49], newest.Entries.Select(OrderId));
        Assert.Equal(Enumerable.Range(0, 50), ReadAllBackward(merged, 8, LogQueryMatcher.All));
    }

    [Fact]
    public void EqualTimes_AreOrderedByMember()
    {
        using var a = new ClefFile().Append(ClefFile.Line(1, message: "a1"), ClefFile.Line(2, message: "a2"));
        using var b = new ClefFile().Append(ClefFile.Line(1, message: "b1"), ClefFile.Line(2, message: "b2"));
        var merged = new MergedSource("m", "m", null, [Source(a), Source(b)]);

        var all = merged.ReadBefore(null, 10, LogQueryMatcher.All, CancellationToken.None).Entries.Select(e => e.Message);
        Assert.Equal(["a1", "b1", "a2", "b2"], all);

        // Each page boundary falls between entries with the same time.
        var messages = new List<string>();
        long? before = null;
        do
        {
            var page = merged.ReadBefore(before, 1, LogQueryMatcher.All, CancellationToken.None);
            messages.InsertRange(0, page.Entries.Select(e => e.Message));
            before = page.HasMore ? page.Cursor ?? page.Entries[0].Id : null;
        } while (before is not null);
        Assert.Equal(["a1", "b1", "a2", "b2"], messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SparseSearch_WithMembersStoppingEarly_FindsEverythingOnce(bool indexed)
    {
        var (even, odd) = Interleaved(4_000);
        using var _ = even;
        using var __ = odd;
        var members = new[] { Source(even, indexed), Source(odd, indexed) };
        foreach (var member in members)
        {
            member.MaxScanBytes = 4096; // Every read stops after a few lines' worth of the file.
            member.MaxScanTime = TimeSpan.Zero;
        }
        var merged = new MergedSource("m", "m", null, members);
        var matcher = LogQueryMatcher.Create(new LogQuery(Text: "77"));
        var expected = Enumerable.Range(0, 4_000).Where(i => i.ToString().Contains("77")).ToList();

        Assert.Equal(expected, ReadAllBackward(merged, 5, matcher));
        Assert.Equal(expected, ReadAllForward(merged, 5, matcher));
    }

    [Fact]
    public void MembersStoppingBeforeTheTimeOfTheAnchor_DoNotRepeatEntries()
    {
        // Every chunk of the older file has an entry without a time, so its index cannot skip by time, and with a small
        // budget a read up to or from the anchor's time stops many times before it gets there.
        using var older = new ClefFile().Append(Enumerable.Range(0, 3_000)
            .Select(i => i % 50 == 25 ? $$"""{"@mt":"Order {OrderId} handled","OrderId":{{i}}}""" : ClefFile.Line(i)).ToArray());
        using var current = new ClefFile().AppendMany(300, from: 3_000);
        var members = new[] { Source(current), Source(older) };
        foreach (var member in members)
        {
            member.MaxScanBytes = 2048;
            member.MaxScanTime = TimeSpan.Zero;
        }
        var merged = new MergedSource("m", "m", null, members);

        Assert.Equal(Enumerable.Range(0, 3_300), ReadAllBackward(merged, 100, LogQueryMatcher.All));
        Assert.Equal(Enumerable.Range(0, 3_300), ReadAllForward(merged, 100, LogQueryMatcher.All));
    }

    [Fact]
    public void Find_ReturnsEntryWithMergedId()
    {
        var (even, odd) = Interleaved(10);
        using var _ = even;
        using var __ = odd;
        var merged = new MergedSource("m", "m", null, [Source(even), Source(odd)]);
        var entry = merged.ReadBefore(null, 3, LogQueryMatcher.All, CancellationToken.None).Entries[1];

        var found = merged.Find(entry.Id);

        Assert.NotNull(found);
        Assert.Equal(entry.Id, found.Id);
        Assert.Equal(8, OrderId(found));
        Assert.Equal(0, MergedSource.Decode(entry.Id).Member);
    }

    [Fact]
    public void Filters_ApplyToEveryMember()
    {
        var even = new ClefFile().AppendMany(10, errorEvery: 3);
        var odd = new ClefFile().AppendMany(10, errorEvery: 4, from: 10);
        using var _ = even;
        using var __ = odd;
        var merged = new MergedSource("m", "m", null, [Source(even), Source(odd)]);

        var errors = merged.ReadBefore(null, 100, LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Error])), CancellationToken.None);

        Assert.Equal([0, 3, 6, 9, 12, 16], errors.Entries.Select(OrderId));
    }

    [Fact]
    public void PositionsAfter_StartEachMemberAfterTheId()
    {
        var (even, odd) = Interleaved(10);
        using var _ = even;
        using var __ = odd;
        var members = new[] { Source(even), Source(odd) };
        var merged = new MergedSource("m", "m", null, members);
        var fifth = merged.ReadBefore(null, 10, LogQueryMatcher.All, CancellationToken.None).Entries[4]; // order 4, in the even file

        var positions = merged.PositionsAfter(fifth.Id, CancellationToken.None);

        Assert.Equal([6, 8], members[0].ReadAfter(positions[0]!.Value, 10, LogQueryMatcher.All, CancellationToken.None).Entries.Select(OrderId));
        Assert.Equal([5, 7, 9], members[1].ReadAfter(positions[1]!.Value, 10, LogQueryMatcher.All, CancellationToken.None).Entries.Select(OrderId));
    }

    [Fact]
    public void Version_ChangesWithMembers()
    {
        var (even, odd) = Interleaved(4);
        using var _ = even;
        using var __ = odd;
        var a = Source(even);
        var b = Source(odd);

        Assert.Equal(new MergedSource("m", "m", null, [a, b]).Version, new MergedSource("m", "m", null, [a, b]).Version);
        Assert.NotEqual(new MergedSource("m", "m", null, [a, b]).Version, new MergedSource("m", "m", null, [a, null]).Version);
        Assert.NotEqual(new MergedSource("m", "m", null, [a, b]).Version, new MergedSource("m", "m", null, [b, a]).Version);
    }

    [Fact]
    public void Registry_MergesFilesWithTheSameDescription()
    {
        using var file = new ClefFile("app.clef").AppendMany(5, from: 10);
        File.WriteAllText(file.Path + ".1", string.Concat(Enumerable.Range(0, 10).Select(i => ClefFile.Line(i) + "\n")));
        var options = new FileSourceOptions
        {
            Pattern = "*.clef*",
            Merge = true,
            Describe = path => new FileDescription("app") { Group = "api" },
        };
        var registry = new FileSourceRegistry([options], file.Directory, TimeProvider.System);

        var merged = Assert.IsType<MergedSource>(Assert.Single(registry.Views));
        Assert.Equal(("app", "api", 2), (merged.Name, merged.Group, merged.Present.Count()));
        Assert.Equal(Enumerable.Range(0, 15), ReadAllBackward(merged, 4, LogQueryMatcher.All));

        var version = merged.Version;
        File.WriteAllText(file.Path + ".2", ClefFile.Line(-5) + "\n");
        registry.Rescan();
        var after = Assert.IsType<MergedSource>(Assert.Single(registry.Views));
        Assert.Equal(merged.Id, after.Id);
        Assert.NotEqual(version, after.Version);
        Assert.Equal(16, ReadAllBackward(after, 4, LogQueryMatcher.All).Count);
    }

    [Fact]
    public void Registry_KeepsMergedSourceForASingleFile()
    {
        using var file = new ClefFile("app.clef").AppendMany(3);
        var options = new FileSourceOptions { Pattern = "*.clef*", Merge = true, Describe = _ => new FileDescription("app") };
        var registry = new FileSourceRegistry([options], file.Directory, TimeProvider.System);

        var merged = Assert.IsType<MergedSource>(Assert.Single(registry.Views));
        Assert.Single(merged.Present);
        Assert.Single(registry.Sources);
    }
}
