using System.Net.Http.Json;
using System.Text.Json;
using Kododo.MonitorWay.Tests.Files;
using Kododo.MonitorWay.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Kododo.MonitorWay.Tests.Integration;

public class AroundApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Around_Id_ReturnsUnfilteredContext()
    {
        using var file = new ClefFile().AppendMany(100, errorEvery: 50);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);
        var error = (await PostAsync<PageDto>(app, "GetEntries", new { sourceId = id, query = new { levels = new[] { "Error" } } }))
            .Entries.Single(e => e.Message == "Order 50 handled");

        var around = await PostAsync<AroundDto>(app, "GetAround", new { sourceId = id, id = error.Id, count = 10 });

        Assert.Equal(error.Id, around.AnchorId);
        Assert.Equal(Enumerable.Range(45, 11).Select(i => $"Order {i} handled"), around.Entries.Select(e => e.Message));
        Assert.True(around.HasOlder);
        Assert.True(around.HasNewer);
    }

    [Fact]
    public async Task Around_Time_AnchorsOnFirstEntryAtOrAfter()
    {
        using var file = new ClefFile().AppendMany(1_000);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        var around = await PostAsync<AroundDto>(app, "GetAround",
            new { sourceId = id, time = ClefFile.TimeOf(500).AddMilliseconds(1), count = 4 });

        var anchor = around.Entries.Single(e => e.Id == around.AnchorId);
        Assert.Equal("Order 501 handled", anchor.Message);
        Assert.Equal(["Order 499 handled", "Order 500 handled", "Order 501 handled", "Order 502 handled", "Order 503 handled"],
            around.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Around_TimeAfterLastEntry_AnchorsOnNewest()
    {
        using var file = new ClefFile().AppendMany(10);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        var around = await PostAsync<AroundDto>(app, "GetAround",
            new { sourceId = id, time = ClefFile.TimeOf(1_000), count = 4 });

        Assert.Equal("Order 9 handled", around.Entries.Single(e => e.Id == around.AnchorId).Message);
        Assert.False(around.HasNewer);
    }

    [Fact]
    public async Task Around_Time_AppliesQuery()
    {
        using var file = new ClefFile().AppendMany(1_000, errorEvery: 100);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        var around = await PostAsync<AroundDto>(app, "GetAround",
            new { sourceId = id, time = ClefFile.TimeOf(450), count = 4, query = new { levels = new[] { "Error" } } });

        Assert.Equal(["Order 300 handled", "Order 400 handled", "Order 500 handled", "Order 600 handled", "Order 700 handled"],
            around.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Around_WorksForMemory()
    {
        using var file = new ClefFile();
        await using var app = await StartAsync(file);
        var logger = app.Services.GetRequiredService<ILogger<AroundApiTests>>();
        for (var i = 0; i < 20; i++)
            logger.LogInformation("m{Index}", i);

        var around = await PostAsync<AroundDto>(app, "GetAround", new { sourceId = "memory", id = 10, count = 4 });

        Assert.Equal(["m7", "m8", "m9", "m10", "m11"], around.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Around_Position_SeeksIntoTheFile()
    {
        using var file = new ClefFile().AppendMany(1_000);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);
        var length = new FileInfo(file.Path).Length;

        var around = await PostAsync<AroundDto>(app, "GetAround", new { sourceId = id, position = length / 2, count = 2 });

        var order = int.Parse(around.Entries.Single(e => e.Id == around.AnchorId).Message.Split(' ')[1]);
        Assert.InRange(order, 495, 505);
    }

    [Fact]
    public async Task Overview_File_CountsEntriesAlongTheFile()
    {
        using var file = new ClefFile().AppendMany(20_000, errorEvery: 10);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);
        await PostAsync<PageDto>(app, "GetEntries", new { sourceId = id });

        OverviewDto overview;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            overview = await PostAsync<OverviewDto>(app, "GetOverview", new { sourceId = id, buckets = 10 });
        } while (overview.Buckets.Sum(b => b.Total) < 20_000 && DateTime.UtcNow < deadline);

        Assert.Equal(1, overview.Start);
        Assert.Equal(new FileInfo(file.Path).Length + 1, overview.End);
        Assert.Equal(10, overview.Buckets.Length);
        Assert.Equal(20_000, overview.Buckets.Sum(b => b.Total));
        Assert.Equal(2_000, overview.Buckets.Sum(b => b.Errors));
        Assert.All(overview.Buckets, b => Assert.InRange(b.Total, 1_500, 2_500));
    }

    [Fact]
    public async Task Overview_Memory_CountsLevels()
    {
        using var file = new ClefFile();
        await using var app = await StartAsync(file);
        var logger = app.Services.GetRequiredService<ILogger<AroundApiTests>>();
        for (var i = 0; i < 100; i++)
        {
            if (i % 4 == 0)
                logger.LogWarning("w");
            else if (i % 4 == 1)
                logger.LogError("e");
            else
                logger.LogInformation("i");
        }

        var overview = await PostAsync<OverviewDto>(app, "GetOverview", new { sourceId = "memory", buckets = 4 });

        Assert.Equal(1, overview.Start);
        Assert.Equal(101, overview.End);
        Assert.Equal([25L, 25, 25, 25], overview.Buckets.Select(b => b.Total));
        Assert.Equal(25, overview.Buckets.Sum(b => b.Warnings));
        Assert.Equal(25, overview.Buckets.Sum(b => b.Errors));
    }

    [Fact]
    public async Task Categories_Memory_MostFrequentFirst()
    {
        using var file = new ClefFile();
        await using var app = await StartAsync(file);
        // The host's logging filter lets only this class's category through, so other categories go straight to the buffer.
        var buffer = app.Services.GetRequiredService<Kododo.MonitorWay.Memory.MemoryLogBuffer>();
        foreach (var (category, times) in new[] { ("Shop.Orders", 3), ("Shop.Cache", 1) })
        {
            for (var i = 0; i < times; i++)
                buffer.Append(Kododo.MonitorWay.Memory.EntryCapture.Create(DateTimeOffset.UtcNow, LogLevel.Information, category, "m", null, null, [], null, null));
        }

        var categories = await PostAsync<CategoryDto[]>(app, "GetCategories", new { sourceId = "memory" });

        Assert.Equal([("Shop.Orders", 3L), ("Shop.Cache", 1L)], categories.Select(c => (c.Name, c.Count)));
    }

    [Fact]
    public async Task GetEntries_SupportsSearchSyntax()
    {
        using var file = new ClefFile().AppendMany(100, errorEvery: 10);
        await using var app = await StartAsync(file);
        var id = await FileIdAsync(app);

        var page = await PostAsync<PageDto>(app, "GetEntries",
            new { sourceId = id, query = new { text = "OrderId:5* -OrderId:50" } });

        Assert.Equal(Enumerable.Range(51, 9).Prepend(5).Select(i => $"Order {i} handled"), page.Entries.Select(e => e.Message));
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
        builder.Logging.AddFilter((category, _) => category == typeof(AroundApiTests).FullName);
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

    private sealed record SourceDto(string Id, string Kind);
    private sealed record RowDto(long Id, string Message, string? TraceId);
    private sealed record PageDto(RowDto[] Entries);
    private sealed record AroundDto(RowDto[] Entries, long? AnchorId, bool HasOlder, bool HasNewer);
    private sealed record BucketDto(long Total, long Warnings, long Errors);
    private sealed record CategoryDto(string Name, long Count);
    private sealed record OverviewDto(long Start, long End, BucketDto[] Buckets);
}
