using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.UI;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Kododo.MonitorWay.Tests.Integration;

public class TraceApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource Source = new(nameof(TraceApiTests));

    [Fact]
    public async Task RequestsOfTheApplication_AreTraces_AndTheUiOwnAreNot()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        Assert.Equal("ok", await client.GetStringAsync("/orders/42"));
        await client.PostAsJsonAsync("/monitor/api/GetTraceStats", new { });
        app.Services.GetRequiredService<MemoryTraceStore>().Tick();

        // The instrumentation listens to every server in the process, so requests of tests running alongside come too.
        var page = await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "http.route:/orders/{id}" } });
        var row = Assert.Single(page.Traces);
        var own = await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "url.path:/monitor*" } });
        Assert.Empty(own.Traces);
        Assert.Equal("GET /orders/{id}", row.Name);
        Assert.Equal("Server", row.Kind);
        Assert.Equal("shop", row.Service);
        Assert.Equal("200", row.StatusCode);
        Assert.Equal(2, row.SpanCount);
        Assert.Equal(0, row.ErrorCount);

        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId = row.TraceId });
        Assert.True(trace.IsComplete);
        var load = Assert.Single(trace.Spans, s => s.Name == "LoadOrder");
        Assert.Equal(trace.Spans.Single(s => s.ParentSpanId is null).SpanId, load.ParentSpanId);
        Assert.Contains(load.Attributes, a => a.Key == "order.id" && a.Value == "42");
        Assert.True(load.OffsetMs >= 0);
    }

    [Fact]
    public async Task FailedRequest_HasTheErrorAndTheLogsOfItsTrace()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await client.GetAsync("/fail");
        app.Services.GetRequiredService<MemoryTraceStore>().Tick();

        var page = await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "status:error http.route:/fail" } });
        var row = Assert.Single(page.Traces);
        Assert.Equal("500", row.StatusCode);
        Assert.True(row.ErrorCount > 0);

        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId = row.TraceId });
        var failed = trace.Spans.Single(s => s.Name == "Charge");
        Assert.Equal("Error", failed.Status);
        Assert.Contains(failed.Events, e => e.Name == "exception" &&
            e.Attributes.Any(a => a.Key == "exception.type" && a.Value == "System.TimeoutException"));

        // The logs written in the request carry its trace id.
        var logs = await PostAsync<EntriesPageDto>(app, "GetEntries",
            new { sourceId = "memory", query = new { text = $"trace:{row.TraceId}" } });
        Assert.Contains(logs.Entries, e => e.Message == "Charging failed");
    }

    [Fact]
    public async Task GetTraces_ReportsAnInvalidQuery()
    {
        await using var app = await StartAsync();

        var page = await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "duration:>soon" } });

        Assert.Empty(page.Traces);
        Assert.Contains("duration", page.Error);
    }

    [Fact]
    public async Task GetTrace_UnknownId_ReturnsNull()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetTrace", new { traceId = new string('0', 32) });

        response.EnsureSuccessStatusCode();
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetTraceStats_TellsWhetherTracesAreExported()
    {
        await using var app = await StartAsync();
        await using var withoutTracing = await StartAsync(tracing: false);

        Assert.True((await PostAsync<TraceStatsDto>(app, "GetTraceStats", new { })).Exported);
        Assert.False((await PostAsync<TraceStatsDto>(withoutTracing, "GetTraceStats", new { })).Exported);
    }

    private static async Task<WebApplication> StartAsync(bool tracing = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter((category, _) => category == typeof(TraceApiTests).FullName);
        builder.AddMonitorWay(x =>
        {
            x.ConfigureTraces(t => t.CompletionDelay = TimeSpan.Zero);
            x.AddUi();
        });
        builder.Services.AddWayShell();
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("shop"))
            .WithLogging(l => l.AddMonitorWay());
        if (tracing)
            otel.WithTracing(t => t.AddSource(nameof(TraceApiTests)).AddAspNetCoreInstrumentation().AddMonitorWay());

        var app = builder.Build();
        app.MapGet("/orders/{id}", (int id) =>
        {
            using var activity = Source.StartActivity("LoadOrder");
            activity?.SetTag("order.id", id);
            return "ok";
        });
        app.MapGet("/fail", (ILogger<TraceApiTests> logger) =>
        {
            using (var activity = Source.StartActivity("Charge"))
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    ["exception.type"] = "System.TimeoutException",
                    ["exception.message"] = "no answer",
                }));
            }
            logger.LogError("Charging failed");
            return Results.StatusCode(500);
        });
        app.UseMonitorWay();
        await app.StartAsync();
        return app;
    }

    private static async Task<T> PostAsync<T>(WebApplication app, string request, object body)
    {
        var response = await app.GetTestClient().PostAsJsonAsync($"/monitor/api/{request}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed record TraceRowDto(long Id, string TraceId, string Name, string Kind, string? Service, int SpanCount, int ErrorCount, string? StatusCode);
    private sealed record TracesPageDto(TraceRowDto[] Traces, bool HasMore, string? Error);
    private sealed record PropertyDto(string Key, string? Value);
    private sealed record EventDto(string Name, PropertyDto[] Attributes);
    private sealed record SpanDto(string SpanId, string? ParentSpanId, string Name, double OffsetMs, string Status, PropertyDto[] Attributes, EventDto[] Events);
    private sealed record TraceDetailDto(string TraceId, bool IsComplete, SpanDto[] Spans);
    private sealed record TraceStatsDto(bool Exported);
    private sealed record EntryRowDto(string Message);
    private sealed record EntriesPageDto(EntryRowDto[] Entries);
}
