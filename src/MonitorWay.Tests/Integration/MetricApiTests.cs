using System.Net.Http.Json;
using System.Text.Json;
using Kododo.MonitorWay.UI;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace Kododo.MonitorWay.Tests.Integration;

public class MetricApiTests
{
    private const string Duration = "Microsoft.AspNetCore.Hosting/http.server.request.duration";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Overview_ShowsTheRoutesWithTheirErrorsAndDurations_NotTheUiOwn()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        await client.GetAsync("/metric-tests/1");
        await client.GetAsync("/metric-tests/2");
        await client.GetAsync("/metric-tests-fail");
        await client.PostAsJsonAsync("/monitor/api/GetMetricStats", new { });
        Collect(app);

        var overview = await PostAsync<OverviewDto>(app, "GetServiceOverview", new { });
        Assert.True(overview.Exported);
        Assert.True(overview.Available);
        // The meter of ASP.NET Core is shared by every server in the process, so tests running alongside add their routes.
        var route = Assert.Single(overview.Routes, r => r.Route == "/metric-tests/{id}");
        Assert.Equal("GET", route.Method);
        Assert.Equal(2, route.Requests);
        Assert.Equal(0, route.Errors);
        Assert.NotNull(route.P95);
        Assert.Equal(1, Assert.Single(overview.Routes, r => r.Route == "/metric-tests-fail").Errors);
        Assert.DoesNotContain(overview.Routes, r => r.Route.StartsWith("/monitor", StringComparison.Ordinal));
        Assert.True(overview.TotalRequests >= 3);
        Assert.Equal(overview.Times.Length, overview.Requests.Length);
    }

    [Fact]
    public async Task Metrics_AreListed_AndReadAsSeries()
    {
        await using var app = await StartAsync();
        await app.GetTestClient().GetAsync("/metric-tests/1");
        Collect(app);

        var metrics = await PostAsync<InstrumentDto[]>(app, "GetMetrics", new { });
        var duration = Assert.Single(metrics, m => m.Key == Duration);
        Assert.Equal("Histogram", duration.Kind);
        Assert.Equal("s", duration.Unit);
        Assert.Contains(duration.Attributes, a => a.Key == "http.route");
        Assert.Equal("p50", duration.Stats[0]);

        var percentiles = await PostAsync<SeriesPageDto>(app, "GetMetricSeries", new { key = Duration, stat = "percentiles" });
        Assert.Equal(["p50", "p95", "p99"], percentiles.Series.Select(s => s.Label));
        Assert.Equal(10, percentiles.ResolutionSeconds);
        Assert.All(percentiles.Series, s => Assert.Equal(percentiles.Times.Length, s.Values.Length));

        var byRoute = await PostAsync<SeriesPageDto>(app, "GetMetricSeries", new
        {
            key = Duration, stat = "count", groupBy = new[] { "http.route" },
            filter = new[] { new { key = "http.route", value = "/metric-tests/{id}" } },
        });
        var series = Assert.Single(byRoute.Series);
        Assert.Equal("/metric-tests/{id}", series.Label);
        Assert.True(series.Total >= 1);

        var invalid = await PostAsync<SeriesPageDto>(app, "GetMetricSeries", new { key = Duration, stat = "last" });
        Assert.NotNull(invalid.Error);
    }

    [Fact]
    public async Task UnknownInstrument_ReturnsNull()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetMetricSeries", new { key = "none/none" });

        response.EnsureSuccessStatusCode();
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddMonitorWay(x => x.AddUi());
        builder.Services.AddWayShell();
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay());

        var app = builder.Build();
        app.MapGet("/metric-tests/{id}", (int id) => "ok");
        app.MapGet("/metric-tests-fail", () => Results.StatusCode(500));
        app.UseMonitorWay();
        await app.StartAsync();
        return app;
    }

    private static void Collect(WebApplication app) =>
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());

    private static async Task<T> PostAsync<T>(WebApplication app, string request, object body)
    {
        var response = await app.GetTestClient().PostAsJsonAsync($"/monitor/api/{request}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed record RouteDto(string? Method, string Route, long Requests, long Errors, double? P95);
    private sealed record OverviewDto(bool Exported, bool Available, long[] Times, double?[] Requests, long TotalRequests, RouteDto[] Routes);
    private sealed record AttributeDto(string Key, int Values);
    private sealed record InstrumentDto(string Key, string Kind, string? Unit, AttributeDto[] Attributes, string[] Stats);
    private sealed record SeriesDto(string Label, double?[] Values, double? Total);
    private sealed record SeriesPageDto(int ResolutionSeconds, long[] Times, SeriesDto[] Series, string? Error);
}
