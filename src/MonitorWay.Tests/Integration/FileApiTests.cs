using System.Net.Http.Json;
using System.Text.Json;
using Kododo.MonitorWay.Tests.Files;
using Kododo.MonitorWay.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Kododo.MonitorWay.Tests.Integration;

public class FileApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Sources_IncludeMatchedFiles()
    {
        using var file = new ClefFile().AppendMany(3);
        await using var app = await StartAsync(file);

        var sources = await PostAsync<SourceDto[]>(app, "GetSources", new { });

        Assert.Equal(["memory", "file"], sources.Select(s => s.Kind));
        Assert.Equal("app.clef", sources[1].Name);
    }

    [Fact]
    public async Task GetEntries_ReadsFile()
    {
        using var file = new ClefFile().AppendMany(10, errorEvery: 4);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries",
            new { sourceId = id, count = 2, query = new { levels = new[] { "Error" } } });

        Assert.Equal(["Order 4 handled", "Order 8 handled"], page.Entries.Select(e => e.Message));
        Assert.True(page.HasMore);
        Assert.NotNull(page.Cursor);
    }

    [Fact]
    public async Task GetStats_DescribesFile()
    {
        using var file = new ClefFile().AppendMany(10, errorEvery: 5);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);
        await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id });

        StatsDto stats;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            stats = await PostAsync<StatsDto>(app, "GetStats", new { sourceId = id });
        } while (stats.File!.IndexedBytes < stats.File.Length && DateTime.UtcNow < deadline);

        Assert.Equal("clef", stats.File.Format);
        Assert.Equal(2, stats.Levels.Single(l => l.Level == "Error").Count);
        Assert.Equal(8, stats.Levels.Single(l => l.Level == "Information").Count);
    }

    [Fact]
    public async Task Stream_SendsAppendedLinesAndResetsOnTruncation()
    {
        using var file = new ClefFile().AppendMany(5);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await app.GetTestClient().GetAsync($"/monitor/api/stream?source={id}",
            HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));

        file.Append(ClefFile.Line(5, LogLevel.Warning));
        var (eventName, data) = await NextEventAsync(reader, cancellation.Token);
        Assert.Equal("entry", eventName);
        Assert.Equal("Order 5 handled", JsonSerializer.Deserialize<EntryRowDto>(data, Json)!.Message);

        File.WriteAllText(file.Path, ClefFile.Line(100) + "\n");
        // The registry refreshes the index in the background; a read makes the source notice sooner.
        await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = id });
        (eventName, _) = await NextEventAsync(reader, cancellation.Token);
        Assert.Equal("reset", eventName);
    }

    private static async Task<(string Event, string Data)> NextEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? eventName = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventName = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName is not null)
                return (eventName, line["data: ".Length..]);
        }
        throw new EndOfStreamException();
    }

    private static async Task<WebApplication> StartAsync(ClefFile file)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = file.Directory,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.AddMonitorWay(x =>
        {
            x.AddFiles("*.clef");
            x.AddUi();
        });
        builder.Services.AddOpenTelemetry().WithLogging(l => l.AddMonitorWay());

        var app = builder.Build();
        app.UseMonitorWay();
        await app.StartAsync();
        return app;
    }

    private static async Task<string> FileIdAsync(WebApplication app) =>
        (await PostAsync<SourceDto[]>(app, "GetSources", new { })).Single(s => s.Kind == "file").Id;

    private static async Task<T> PostAsync<T>(WebApplication app, string request, object body)
    {
        var response = await app.GetTestClient().PostAsJsonAsync($"/monitor/api/{request}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed record SourceDto(string Id, string Name, string Kind);
    private sealed record EntryRowDto(long Id, string Level, string Message);
    private sealed record EntriesPageDto(EntryRowDto[] Entries, bool HasMore, string? Error, long? Cursor, long Version);
    private sealed record LevelStatsDto(string Level, long Count);
    private sealed record FileDto(string Path, long Length, long IndexedBytes, bool Indexing, string? Format, string? Error);
    private sealed record StatsDto(LevelStatsDto[] Levels, FileDto? File);
}
