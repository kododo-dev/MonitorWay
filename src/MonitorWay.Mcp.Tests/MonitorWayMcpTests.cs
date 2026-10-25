using Kododo.Way.Shell;
using System.Net;
using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Kododo.MonitorWay.Mcp.Tests;

public sealed class MonitorWayMcpTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitorway-mcp-tests", Guid.NewGuid().ToString("N"));

    public MonitorWayMcpTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "shop.clef"), string.Concat(
            Line(0, "Information", "Order {OrderId} placed", "Shop.Orders", 1007),
            Line(1, "Error", "Payment for order {OrderId} failed", "Shop.Payments", 1008, "System.TimeoutException: Stripe did not answer\n   at Shop.Payments.Charge()"),
            Line(2, "Information", "Order {OrderId} placed", "Shop.Orders", 1009),
            Line(3, "Warning", "Stock of {OrderId} low", "Shop.Inventory", 1010)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Line(int second, string level, string template, string category, int order, string? exception = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@t"] = new DateTimeOffset(2026, 10, 14, 8, 0, second, TimeSpan.Zero).ToString("O"),
            ["@mt"] = template,
            ["@l"] = level,
            ["@x"] = exception,
            ["OrderId"] = order,
            ["SourceContext"] = category,
        }) + "\n";

    private async Task<WebApplication> StartAsync(string environment = "Development", bool authorize = false, bool allowAnonymous = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = _directory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddMonitorWay(x =>
        {
            x.AddFiles("*.clef");
            x.AddUi();
        });
        builder.Services.AddWayShell(o => o.AllowAnonymous = allowAnonymous);
        builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();
        if (authorize)
        {
            builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AlwaysSignedIn>("test", null);
            builder.Services.AddAuthorization();
        }

        var app = builder.Build();
        var mcp = app.MapMonitorWayMcp();
        if (authorize)
            mcp.RequireAuthorization();
        await app.StartAsync();
        return app;
    }

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

    [Fact]
    public async Task Tools_AreTheReadOnlyApi_WithDescriptions()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app);

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            [
                "monitorway_get_around", "monitorway_get_categories", "monitorway_get_entries", "monitorway_get_entry",
                "monitorway_get_error", "monitorway_get_error_stats", "monitorway_get_errors",
                "monitorway_get_metric_series", "monitorway_get_metric_stats", "monitorway_get_metrics",
                "monitorway_get_service_overview", "monitorway_get_services", "monitorway_get_sources", "monitorway_get_stats",
                "monitorway_get_trace", "monitorway_get_trace_stats", "monitorway_get_traces",
            ],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(tools, t => Assert.False(string.IsNullOrEmpty(t.Description)));
        var query = tools.Single(t => t.Name == "monitorway_get_entries").ProtocolTool.InputSchema.GetProperty("properties").GetProperty("query");
        Assert.Contains("has:exception", query.ToString());
    }

    [Fact]
    public async Task Agent_FindsAnErrorAndReadsItsException()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app);

        var source = Json(await client.CallToolAsync("monitorway_get_sources")).EnumerateArray().Single();
        var errors = Json(await client.CallToolAsync("monitorway_get_entries", new Dictionary<string, object?>
        {
            ["sourceId"] = source.GetProperty("id").GetString(),
            ["count"] = 10,
            ["query"] = new { levels = new[] { "Error" }, text = "payment" },
        })).GetProperty("entries");
        var error = Assert.Single(errors.EnumerateArray());
        var detail = Json(await client.CallToolAsync("monitorway_get_entry", new Dictionary<string, object?>
        {
            ["sourceId"] = source.GetProperty("id").GetString(),
            ["id"] = error.GetProperty("id").GetInt64(),
        }));

        Assert.Equal("shop.clef", source.GetProperty("name").GetString());
        Assert.Equal("Payment for order 1008 failed", error.GetProperty("message").GetString());
        Assert.StartsWith("System.TimeoutException: Stripe did not answer", detail.GetProperty("exception").GetString());
    }

    [Fact]
    public async Task Agent_FiltersByCategory()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app);
        var sourceId = Json(await client.CallToolAsync("monitorway_get_sources")).EnumerateArray().Single().GetProperty("id").GetString();

        var orders = Json(await client.CallToolAsync("monitorway_get_entries", new Dictionary<string, object?>
        {
            ["sourceId"] = sourceId,
            ["query"] = new { categories = new[] { "Shop.Orders" } },
        })).GetProperty("entries");

        Assert.Equal(["Order 1007 placed", "Order 1009 placed"], orders.EnumerateArray().Select(e => e.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task OutsideDevelopment_TheEndpointIsClosedUntilProtected()
    {
        await using var app = await StartAsync(environment: "Production");

        var response = await app.GetTestClient().PostAsync("/monitor/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OutsideDevelopment_AuthorizationOrAllowAnonymous_OpensTheEndpoint(bool authorize, bool allowAnonymous)
    {
        await using var app = await StartAsync(environment: "Production", authorize: authorize, allowAnonymous: allowAnonymous);
        await using var client = await ConnectAsync(app);

        Assert.Equal(17, (await client.ListToolsAsync()).Count);
    }
}
