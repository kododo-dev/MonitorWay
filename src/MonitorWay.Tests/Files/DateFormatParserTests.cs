using System.Globalization;
using Kododo.MonitorWay.Files.Formats;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class DateFormatParserTests
{
    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff zzz", "2026-10-13 09:28:10.512 +02:00")]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff zzz", "2026-10-13 23:59:59.999 -05:30")]
    [InlineData("yyyy-MM-dd HH:mm:ss.ffff", "2026-10-13 09:28:10.5123")]
    [InlineData("yyyy-MM-dd HH:mm:ss,fff", "2026-02-28 00:00:00,001")]
    [InlineData("dd/MM/yyyy HH:mm:ss", "13/10/2026 09:28:10")]
    [InlineData("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "2026-10-13T09:28:10.5Z")]
    [InlineData("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "2026-10-13T09:28:10+01:00")]
    [InlineData("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "2026-10-13T09:28:10.1234567")]
    [InlineData("M/d/yyyy h:mm:ss tt", "10/3/2026 9:05:07 PM")]
    [InlineData("M/d/yyyy h:mm:ss tt", "1/13/2026 12:05:07 AM")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss", "2026-10-13T09:28:10")]
    [InlineData("yyMMdd HHmmss", "261013 092810")]
    public void MatchesParseExact(string format, string text)
    {
        var parser = DateFormatParser.TryCreate(format);

        Assert.NotNull(parser);
        Assert.True(parser.TryParse(text, out var parsed));
        var expected = DateTimeOffset.ParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        Assert.Equal(expected, parsed);
        Assert.Equal(expected.Offset, parsed.Offset);
    }

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-13-01 00:00:00")]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-02-30 00:00:00")]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-10-13 24:00:00")]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-10-13 09:28")]
    [InlineData("yyyy-MM-dd HH:mm:ss", "2026-10-13 09:28:10 extra")]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", "2026-10-13 09:28:10.5")]
    [InlineData("yyyy-MM-dd HH:mm:ss zzz", "2026-10-13 09:28:10 0200")]
    public void RejectsInvalidText(string format, string text)
    {
        Assert.False(DateFormatParser.TryCreate(format)!.TryParse(text, out _));
    }

    [Fact]
    public void TimeOnlyFormat_UsesTheDefaultDate()
    {
        var parser = DateFormatParser.TryCreate("HH:mm:ss.fff", new DateOnly(2026, 10, 3))!;

        Assert.True(parser.TryParse("09:28:10.512", out var value));
        Assert.Equal(new DateTime(2026, 10, 3, 9, 28, 10, 512), value.DateTime);
    }

    [Fact]
    public void TimeOnlyTemplate_TakesTheDateFromTheFileName()
    {
        using var file = new ClefFile("app-20261003.log");
        file.AppendRaw("09:28:10 INF Order placed\n");
        var options = new Kododo.MonitorWay.Files.FileSourceOptions { Pattern = "*" }.UseTemplate("{Timestamp:HH:mm:ss} {Level:u3} {Message}");
        var source = new Kododo.MonitorWay.Files.FileLogSource(file.Path, "app.log", options);

        var entry = source.ReadAfter(0, 1, Kododo.MonitorWay.Querying.LogQueryMatcher.All, CancellationToken.None).Entries.Single();

        Assert.Equal(new DateTime(2026, 10, 3, 9, 28, 10), entry.Timestamp!.Value.DateTime);
    }

    [Fact]
    public void TimeOnlyTemplate_WithoutDateInName_TakesTheLastWriteDay()
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw("09:28:10 INF Order placed\n");
        File.SetLastWriteTime(file.Path, new DateTime(2026, 9, 1, 23, 0, 0));
        var options = new Kododo.MonitorWay.Files.FileSourceOptions { Pattern = "*" }.UseTemplate("{Timestamp:HH:mm:ss} {Level:u3} {Message}");
        var source = new Kododo.MonitorWay.Files.FileLogSource(file.Path, "app.log", options);

        var entry = source.ReadAfter(0, 1, Kododo.MonitorWay.Querying.LogQueryMatcher.All, CancellationToken.None).Entries.Single();

        Assert.Equal(new DateOnly(2026, 9, 1), DateOnly.FromDateTime(entry.Timestamp!.Value.DateTime));
    }

    [Theory]
    [InlineData("MMM d, yyyy")]
    [InlineData("dddd HH:mm")]
    [InlineData("HH:mm z")]
    public void NamesAndShortOffsets_AreLeftToParseExact(string format)
    {
        Assert.Null(DateFormatParser.TryCreate(format));
    }
}
