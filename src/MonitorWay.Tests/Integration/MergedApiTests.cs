using System.Net.Http.Json;
using System.Text.Json;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Tests.Files;
using Kododo.MonitorWay.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Integration;

public class MergedApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SourcesJoinedWithComma_AreReadTogetherByTime()
    {
        using var dir = new ClefFile("a.clef");
        File.WriteAllText(dir.Path, Lines(0, 2, 4));
        File.WriteAllText(Path.Combine(dir.Directory, "b.clef"), Lines(1, 3, 5));
        await using var app = await StartAsync(dir.Directory);
        var id = await MergedIdAsync(app);

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id, count = 4 });

        Assert.Equal(["Order 2 handled", "Order 3 handled", "Order 4 handled", "Order 5 handled"], page.Entries.Select(e => e.Message));
        Assert.True(page.HasMore);
        var older = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id, before = page.Entries[0].Id });
        Assert.Equal(["Order 0 handled", "Order 1 handled"], older.Entries.Select(e => e.Message));

        var entry = await PostAsync<EntryRowDto>(app, "GetEntry", new { sourceId = id, id = page.Entries[1].Id });
        Assert.Equal("Order 3 handled", entry.Message);
    }

    [Fact]
    public async Task SourcesJoinedWithComma_NameTheMemberOfEachEntry()
    {
        using var dir = new ClefFile("a.clef");
        File.WriteAllText(dir.Path, Lines(0, 2));
        File.WriteAllText(Path.Combine(dir.Directory, "b.clef"), Lines(1, 3));
        await using var app = await StartAsync(dir.Directory);
        var id = await MergedIdAsync(app);

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id });

        Assert.Equal(["a.clef", "b.clef", "a.clef", "b.clef"], page.Entries.Select(e => e.Member));
        var around = await PostAsync<AroundPageDto>(app, "GetAround", new { sourceId = id, id = page.Entries[1].Id, count = 2 });
        Assert.Equal("b.clef", around.Entries.Single(e => e.Id == around.AnchorId).Member);
        var single = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id.Split(',')[0] });
        Assert.All(single.Entries, e => Assert.Null(e.Member));
    }

    [Fact]
    public async Task RotatedFilesOfOneLog_AreAFileWithoutMembers()
    {
        using var dir = new ClefFile("app.clef");
        File.WriteAllText(dir.Path, Lines(2, 3));
        File.WriteAllText(Path.Combine(dir.Directory, "app.1.clef"), Lines(0, 1));
        await using var app = await StartAsync(dir.Directory, f =>
        {
            f.Merge = true;
            f.Describe = _ => new FileDescription("app");
        });

        var source = Assert.Single(await PostAsync<SourceDto[]>(app, "GetSources", new { }));
        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = source.Id });

        Assert.Equal("file", source.Kind);
        Assert.Equal(4, page.Entries.Length);
        Assert.All(page.Entries, e => Assert.Null(e.Member));
    }

    [Fact]
    public async Task StatsCategoriesAndOverview_SumTheMembers()
    {
        using var dir = new ClefFile("a.clef");
        File.WriteAllText(dir.Path, Lines(0, 2, 4));
        File.WriteAllText(Path.Combine(dir.Directory, "b.clef"), Lines(1, 3));
        await using var app = await StartAsync(dir.Directory);
        var id = await MergedIdAsync(app);
        await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id });

        StatsDto stats;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            stats = await PostAsync<StatsDto>(app, "GetStats", new { sourceId = id });
        } while (stats.File!.IndexedBytes < stats.File.Length && DateTime.UtcNow < deadline);

        Assert.Equal(5, stats.Levels.Single(l => l.Level == "Information").Count);
        Assert.Equal("2 files", stats.File.Path);
        var categories = await PostAsync<CategoryDto[]>(app, "GetCategories", new { sourceId = id });
        Assert.Equal(5, Assert.Single(categories).Count);

        // The overview of a merged source is in time space, and a position on it is a moment.
        var overview = await PostAsync<OverviewDto>(app, "GetOverview", new { sourceId = id, buckets = 5 });
        Assert.True(overview.Time);
        Assert.Equal(ClefFile.TimeOf(0).ToUnixTimeMilliseconds(), overview.Start);
        Assert.Equal(5, overview.Buckets.Sum(b => b.Total));
        var around = await PostAsync<AroundPageDto>(app, "GetAround", new { sourceId = id, position = ClefFile.TimeOf(3).ToUnixTimeMilliseconds(), count = 2 });
        Assert.Equal("Order 3 handled", around.Entries.Single(e => e.Id == around.AnchorId).Message);
    }

    [Fact]
    public async Task Stream_FollowsEveryMember()
    {
        using var dir = new ClefFile("a.clef");
        File.WriteAllText(dir.Path, Lines(0));
        var b = Path.Combine(dir.Directory, "b.clef");
        File.WriteAllText(b, Lines(1));
        await using var app = await StartAsync(dir.Directory);
        var id = await MergedIdAsync(app);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await app.GetTestClient().GetAsync($"/monitor/api/stream?source={Uri.EscapeDataString(id)}",
            HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));

        // The second file gets an entry older than one already in the first, as a replica a moment behind would.
        File.AppendAllText(dir.Path, Lines(10));
        var first = await NextEntryAsync(reader, cancellation.Token);
        Assert.Equal(("Order 10 handled", "a.clef"), (first.Message, first.Member));
        File.AppendAllText(b, Lines(9));
        var second = await NextEntryAsync(reader, cancellation.Token);
        Assert.Equal(("Order 9 handled", "b.clef"), (second.Message, second.Member));
    }

    private static string Lines(params int[] orders) => string.Concat(orders.Select(i => ClefFile.Line(i) + "\n"));

    private static async Task<EntryRowDto> NextEntryAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"message\""))
                return JsonSerializer.Deserialize<EntryRowDto>(line["data: ".Length..], Json)!;
        }
        throw new EndOfStreamException();
    }

    private static async Task<WebApplication> StartAsync(string directory, Action<FileSourceOptions>? files = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = directory,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.AddMonitorWay(x =>
        {
            x.AddFiles("*.clef", files);
            x.AddUi();
        });

        var app = builder.Build();
        app.UseMonitorWay();
        await app.StartAsync();
        return app;
    }

    private static async Task<string> MergedIdAsync(WebApplication app) =>
        string.Join(',', (await PostAsync<SourceDto[]>(app, "GetSources", new { })).Select(s => s.Id));

    private static async Task<T> PostAsync<T>(WebApplication app, string request, object body)
    {
        var response = await app.GetTestClient().PostAsJsonAsync($"/monitor/api/{request}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed record SourceDto(string Id, string Name, string Kind);
    private sealed record EntryRowDto(long Id, string Level, string Message, string? Member);
    private sealed record EntriesPageDto(EntryRowDto[] Entries, bool HasMore, string? Error, long? Cursor, long Version);
    private sealed record AroundPageDto(EntryRowDto[] Entries, long? AnchorId);
    private sealed record CategoryDto(string Name, long Count);
    private sealed record OverviewBucketDto(long Total);
    private sealed record OverviewDto(long Start, long End, OverviewBucketDto[] Buckets, bool Time);
    private sealed record LevelStatsDto(string Level, long Count);
    private sealed record FileDto(string Path, long Length, long IndexedBytes, bool Indexing, string? Format, string? Error);
    private sealed record StatsDto(LevelStatsDto[] Levels, FileDto? File);
}
