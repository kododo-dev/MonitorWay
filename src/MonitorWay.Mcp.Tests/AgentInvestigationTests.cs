using System.Diagnostics;
using System.Text.Json;
using Kododo.MonitorWay.UI;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Kododo.MonitorWay.Mcp.Tests;

/// <summary>
/// An agent investigates a failing endpoint with the tools alone: from the overview to the error, to its trace and to the
/// logs of that request, then to the metric behind the overview.
/// </summary>
public sealed class AgentInvestigationTests
{
    private static readonly ActivitySource Source = new(nameof(AgentInvestigationTests));

    [Fact]
    public async Task FromTheOverview_ToTheErrorItsTraceAndItsLogs()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();
        for (var i = 0; i < 3; i++)
            await http.PostAsync("/mcp-tests/pay", null);
        await http.GetAsync("/mcp-tests/ok");
        Collect(app);
        await using var client = await ConnectAsync(app);

        // The overview: which route fails.
        var overview = Json(await client.CallToolAsync("monitorway_get_service_overview"));
        var route = overview.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("route").GetString() == "/mcp-tests/pay");
        Assert.Equal(3, route.GetProperty("errors").GetInt64());

        // The error behind it, grouped once although each occurrence was logged and recorded on its span.
        var errors = Json(await client.CallToolAsync("monitorway_get_errors", new Dictionary<string, object?>
        {
            ["query"] = new { text = "endpoint:POST*" },
        })).GetProperty("errors");
        var group = Assert.Single(errors.EnumerateArray());
        Assert.Equal("System.TimeoutException", group.GetProperty("type").GetString());
        Assert.Equal(3, group.GetProperty("count").GetInt64());

        var error = Json(await client.CallToolAsync("monitorway_get_error", new Dictionary<string, object?>
        {
            ["fingerprint"] = group.GetProperty("fingerprint").GetString(),
        }));
        Assert.Contains("AgentInvestigationTests.Charge", error.GetProperty("stackTrace").GetString());
        var occurrence = error.GetProperty("occurrences")[0];
        Assert.True(occurrence.GetProperty("fromSpan").GetBoolean());
        Assert.True(occurrence.GetProperty("fromLog").GetBoolean());
        var traceId = occurrence.GetProperty("traceId").GetString();

        // Its trace: the failed call within the request.
        var trace = Json(await client.CallToolAsync("monitorway_get_trace", new Dictionary<string, object?> { ["traceId"] = traceId }));
        var call = trace.GetProperty("spans").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "POST payments");
        Assert.Equal("Error", call.GetProperty("status").GetString());

        // The logs of that request.
        var logs = Json(await client.CallToolAsync("monitorway_get_entries", new Dictionary<string, object?>
        {
            ["sourceId"] = "memory",
            ["query"] = new { text = $"trace:{traceId}" },
        })).GetProperty("entries");
        Assert.Contains(logs.EnumerateArray(), e => e.GetProperty("message").GetString() == "Payment for order 7 failed");

        // The failed traces are found by a search too.
        var failed = Json(await client.CallToolAsync("monitorway_get_traces", new Dictionary<string, object?>
        {
            ["count"] = 10,
            ["query"] = new { text = "status:error http.route:/mcp-tests/pay" },
        })).GetProperty("traces");
        Assert.Equal(3, failed.GetArrayLength());
    }

    [Fact]
    public async Task Metrics_AreListedAndRead()
    {
        await using var app = await StartAsync();
        await app.GetTestClient().GetAsync("/mcp-tests/ok");
        Collect(app);
        await using var client = await ConnectAsync(app);

        var metrics = Json(await client.CallToolAsync("monitorway_get_metrics"));
        var duration = metrics.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "http.server.request.duration");

        var series = Json(await client.CallToolAsync("monitorway_get_metric_series", new Dictionary<string, object?>
        {
            ["key"] = duration.GetProperty("key").GetString(),
            ["stat"] = "count",
            ["groupBy"] = new[] { "http.route" },
            ["filter"] = new[] { new { key = "http.route", value = "/mcp-tests/ok" } },
        }));
        Assert.Equal(1, series.GetProperty("series")[0].GetProperty("total").GetDouble());

        var stats = Json(await client.CallToolAsync("monitorway_get_metric_stats"));
        Assert.True(stats.GetProperty("exported").GetBoolean());
        Assert.True(Json(await client.CallToolAsync("monitorway_get_trace_stats")).GetProperty("exported").GetBoolean());
        Assert.True(Json(await client.CallToolAsync("monitorway_get_error_stats")).GetProperty("exported").GetBoolean());
    }

    private static void Charge() => throw new TimeoutException("PayU did not answer within 10 seconds.");

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter((category, _) => category == typeof(AgentInvestigationTests).FullName);
        builder.AddMonitorWay(x =>
        {
            x.ConfigureTraces(t => t.CompletionDelay = TimeSpan.Zero);
            x.AddUi();
        });
        builder.Services.AddWayShell();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("shop"))
            .WithTracing(t => t.AddSource(nameof(AgentInvestigationTests)).AddAspNetCoreInstrumentation().AddMonitorWay())
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMonitorWay())
            .WithLogging(l => l.AddMonitorWay());
        builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();

        var app = builder.Build();
        app.MapPost("/mcp-tests/pay", (ILogger<AgentInvestigationTests> logger) =>
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
                logger.LogError(e, "Payment for order {OrderId} failed", 7);
            }
            return Results.StatusCode(502);
        });
        app.MapGet("/mcp-tests/ok", () => "ok");
        app.UseMonitorWay();
        app.MapMonitorWayMcp();
        await app.StartAsync();
        return app;
    }

    private static void Collect(WebApplication app) =>
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());

    private static Task<McpClient> ConnectAsync(WebApplication app)
    {
        var http = app.GetTestClient();
        return McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/monitor/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http));
    }

    private static JsonElement Json(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
    }
}
