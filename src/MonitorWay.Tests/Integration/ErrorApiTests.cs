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

public class ErrorApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource Source = new(nameof(ErrorApiTests));

    [Fact]
    public async Task ExceptionLoggedAndRecordedOnItsSpan_IsOneOccurrence()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        await client.GetAsync("/pay");
        await client.GetAsync("/pay");
        app.Services.GetRequiredService<MemoryTraceStore>().Tick();

        var page = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { query = new { text = "type:Timeout" } });
        var row = Assert.Single(page.Errors);
        Assert.Equal("System.TimeoutException", row.Type);
        Assert.Equal(2, row.Count);
        Assert.Equal(["shop"], row.Services);
        Assert.Equal(["GET /pay"], row.Endpoints);
        Assert.Equal(2, row.Trend[^1]);

        var detail = await PostAsync<ErrorDetailDto>(app, "GetError", new { fingerprint = row.Fingerprint });
        Assert.Contains("ErrorApiTests.Charge", detail.Frames[0]);
        Assert.Contains("PayU did not answer", detail.StackTrace);
        Assert.Equal(2, detail.Occurrences.Length);
        Assert.All(detail.Occurrences, o =>
        {
            Assert.True(o.FromSpan);
            Assert.True(o.FromLog);
            Assert.NotNull(o.TraceId);
        });

        // The log entry and the span event lead to the group.
        var entry = await PostAsync<EntryDetailDto>(app, "GetEntry", new { sourceId = "memory", id = detail.Occurrences[0].LogEntryId });
        Assert.Equal(row.Fingerprint, entry.ErrorFingerprint);
        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId = detail.Occurrences[0].TraceId });
        Assert.Contains(trace.Spans.SelectMany(s => s.Events), e => e.ErrorFingerprint == row.Fingerprint);
    }

    [Fact]
    public async Task LoggedExceptionWithoutTracing_IsAnError()
    {
        await using var app = await StartAsync(tracing: false);

        await app.GetTestClient().GetAsync("/pay");

        var page = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { query = new { text = "type:Timeout" } });
        Assert.Equal(1, Assert.Single(page.Errors).Count);
        Assert.True((await PostAsync<ErrorStatsDto>(app, "GetErrorStats", new { })).Exported);
    }

    [Fact]
    public async Task GetErrors_SortsAndReportsAnInvalidQuery()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        await client.GetAsync("/pay");
        await client.GetAsync("/parse");
        await client.GetAsync("/parse");
        app.Services.GetRequiredService<MemoryTraceStore>().Tick();

        var byCount = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { sort = "count", query = new { text = "service:shop" } });
        Assert.Equal("System.FormatException", byCount.Errors[0].Type);

        var invalid = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { query = new { text = "color:red" } });
        Assert.Empty(invalid.Errors);
        Assert.NotNull(invalid.Error);
    }

    [Fact]
    public async Task GetError_Unknown_ReturnsNull()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync("/monitor/api/GetError", new { fingerprint = "0000" });

        response.EnsureSuccessStatusCode();
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    private static void Charge() => throw new TimeoutException("PayU did not answer within 10 seconds.");

    private static async Task<WebApplication> StartAsync(bool tracing = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter((category, _) => category == typeof(ErrorApiTests).FullName);
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
            otel.WithTracing(t => t.AddSource(nameof(ErrorApiTests)).AddAspNetCoreInstrumentation().AddMonitorWay());

        var app = builder.Build();
        app.MapGet("/pay", (ILogger<ErrorApiTests> logger) =>
        {
            using var call = Source.StartActivity("POST payments", ActivityKind.Client);
            try
            {
                Charge();
            }
            catch (TimeoutException e)
            {
                call?.SetStatus(ActivityStatusCode.Error, e.Message);
                call?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    ["exception.type"] = e.GetType().FullName,
                    ["exception.message"] = e.Message,
                    ["exception.stacktrace"] = e.ToString(),
                }));
                logger.LogError(e, "Payment failed");
            }
            return Results.StatusCode(502);
        });
        app.MapGet("/parse", (ILogger<ErrorApiTests> logger) =>
        {
            try
            {
                int.Parse("x", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FormatException e)
            {
                logger.LogWarning(e, "Bad coupon");
            }
            return "ok";
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

    private sealed record ErrorRowDto(string Fingerprint, string Type, long Count, string[] Services, string[] Endpoints, int[] Trend);
    private sealed record ErrorsPageDto(ErrorRowDto[] Errors, string? Error);
    private sealed record OccurrenceDto(string? TraceId, long? LogEntryId, bool FromSpan, bool FromLog);
    private sealed record ErrorDetailDto(string Fingerprint, string? StackTrace, string[] Frames, OccurrenceDto[] Occurrences);
    private sealed record ErrorStatsDto(bool Exported);
    private sealed record EntryDetailDto(string? ErrorFingerprint);
    private sealed record EventDto(string Name, string? ErrorFingerprint);
    private sealed record SpanDto(EventDto[] Events);
    private sealed record TraceDetailDto(SpanDto[] Spans);
}
