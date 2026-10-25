using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kododo.MonitorWay.UI;
using Kododo.Way.Shell;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kododo.MonitorWay.Tests.Integration;

public class ApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetEntries_ReturnsNewestEntries()
    {
        await using var app = await StartAsync();
        var logger = app.Services.GetRequiredService<ILogger<ApiTests>>();
        logger.LogInformation("first");
        logger.LogError("second");

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = "memory" });

        Assert.Equal(["first", "second"], page.Entries.Select(e => e.Message));
        Assert.Equal(["Information", "Error"], page.Entries.Select(e => e.Level));
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task GetEntries_AppliesQuery()
    {
        await using var app = await StartAsync();
        var logger = app.Services.GetRequiredService<ILogger<ApiTests>>();
        logger.LogInformation("order placed");
        logger.LogError("order failed");
        logger.LogError("payment failed");

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries",
            new { sourceId = "memory", query = new { levels = new[] { "Error" }, text = "order" } });

        Assert.Equal(["order failed"], page.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task GetEntries_ReportsInvalidRegex()
    {
        await using var app = await StartAsync();

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries",
            new { sourceId = "memory", query = new { text = "(", regex = true } });

        Assert.Empty(page.Entries);
        Assert.NotNull(page.Error);
    }

    [Fact]
    public async Task GetEntry_ReturnsDetails()
    {
        await using var app = await StartAsync();
        app.Services.GetRequiredService<ILogger<ApiTests>>().LogWarning("Disk {Drive} almost full", "C:");

        var entry = await PostAsync<EntryDetailDto>(app, "GetEntry", new { sourceId = "memory", id = 1 });

        Assert.Equal("Disk {Drive} almost full", entry.MessageTemplate);
        Assert.Contains(entry.Properties, p => p.Key == "Drive" && p.Value == "C:");
    }

    [Fact]
    public async Task GetStats_ReportsLevelsAndPools()
    {
        await using var app = await StartAsync();
        var logger = app.Services.GetRequiredService<ILogger<ApiTests>>();
        logger.LogError("e");
        logger.LogError("e");
        logger.LogInformation("i");

        var stats = await PostAsync<StatsDto>(app, "GetStats", new { sourceId = "memory" });

        Assert.Equal(2, stats.Levels.Single(l => l.Level == "Error").Count);
        Assert.Equal(1, stats.Levels.Single(l => l.Level == "Information").Count);
        Assert.Equal(2, stats.Pools.Length);
    }

    [Fact]
    public async Task GetSources_ListsMemoryBuffer()
    {
        await using var app = await StartAsync();

        var sources = await PostAsync<SourceDto[]>(app, "GetSources", new { });

        Assert.Equal(["memory"], sources.Select(s => s.Id));
    }

    [Fact]
    public async Task OutsideDevelopment_WithoutAuthorization_Returns403()
    {
        await using var app = await StartAsync(environment: Environments.Production);

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetSources", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_StreamWithoutAuthorization_Returns403()
    {
        await using var app = await StartAsync(environment: Environments.Production);

        var response = await app.GetTestClient().GetAsync("/monitor/api/stream", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_AllowAnonymousOption_Returns200()
    {
        await using var app = await StartAsync(environment: Environments.Production, allowAnonymous: true);

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetSources", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_WithRequireAuthorization_Returns200()
    {
        await using var app = await StartAsync(environment: Environments.Production, configureEndpoints: g => g.RequireAuthorization());

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetSources", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stream_SendsEntriesLoggedAfterConnecting()
    {
        await using var app = await StartAsync();
        var logger = app.Services.GetRequiredService<ILogger<ApiTests>>();
        logger.LogInformation("before");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await app.GetTestClient().GetAsync("/monitor/api/stream?after=1&levels=Error",
            HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));

        logger.LogInformation("filtered out");
        logger.LogError("live error");

        string? data = null;
        while (data is null && await reader.ReadLineAsync(cancellation.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line["data: ".Length..];
        }

        var entry = JsonSerializer.Deserialize<EntryRowDto>(data!, Json)!;
        Assert.Equal("live error", entry.Message);
        Assert.Equal(3, entry.Id);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task OwnRequests_AreNotCapturedUnlessAskedFor(bool captureOwnRequests, int expectedAtLeast)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.AddMonitorWay(x => x.AddUi(ui => ui.CaptureOwnRequests = captureOwnRequests));
        builder.Services.AddOpenTelemetry().WithLogging(l => l.AddMonitorWay());
        await using var app = builder.Build();
        app.MapGet("/orders", () => "ok");
        app.UseMonitorWay();
        await app.StartAsync();
        var client = app.GetTestClient();

        await client.PostAsJsonAsync("/monitor/api/GetStats", new { sourceId = "memory" });
        await client.GetAsync("/orders");

        var entries = app.Services.GetRequiredService<Kododo.MonitorWay.Memory.MemoryLogBuffer>()
            .ReadBefore(null, 1000, Kododo.MonitorWay.Querying.LogQueryMatcher.All, out _);
        string? PathOf(Core.Model.LogEntry e) => e.Properties.FirstOrDefault(p => p.Key == "RequestPath").Value;
        Assert.Contains(entries, e => PathOf(e) == "/orders");
        Assert.True(entries.Count(e => PathOf(e)?.StartsWith("/monitor", StringComparison.Ordinal) == true) >= expectedAtLeast);
        if (!captureOwnRequests)
            Assert.DoesNotContain(entries, e => PathOf(e)?.StartsWith("/monitor", StringComparison.Ordinal) == true);
    }

    private static async Task<WebApplication> StartAsync(
        string environment = "Development",
        bool allowAnonymous = false,
        Action<RouteGroupBuilder>? configureEndpoints = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter((category, _) => category == typeof(ApiTests).FullName);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AllowAllHandler>("Test", null);
        builder.Services.AddAuthorization();

        builder.AddMonitorWay(x => x.AddUi());
        builder.Services.AddWayShell(o => o.AllowAnonymous = allowAnonymous);
        builder.Services.AddOpenTelemetry().WithLogging(l => l.AddMonitorWay());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var endpoints = app.UseMonitorWay();
        configureEndpoints?.Invoke(endpoints);

        await app.StartAsync();
        return app;
    }

    private static async Task<T> PostAsync<T>(WebApplication app, string request, object body)
    {
        var response = await app.GetTestClient().PostAsJsonAsync($"/monitor/api/{request}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed class AllowAllHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record SourceDto(string Id, string Name, string Kind);
    private sealed record EntryRowDto(long Id, DateTimeOffset? Timestamp, string Level, string? Category, string Message, bool HasException);
    private sealed record EntriesPageDto(EntryRowDto[] Entries, bool HasMore, string? Error);
    private sealed record PropertyDto(string Key, string? Value);
    private sealed record EntryDetailDto(long Id, string? MessageTemplate, PropertyDto[] Properties);
    private sealed record LevelStatsDto(string Level, int Count, int Pool);
    private sealed record PoolDto(string[] Levels, int Count);
    private sealed record StatsDto(LevelStatsDto[] Levels, PoolDto[] Pools);
}
