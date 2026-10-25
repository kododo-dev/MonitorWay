using Kododo.MonitorWay;
using Kododo.MonitorWay.Demo.Web;
using Kododo.MonitorWay.UI;
using Kododo.MonitorWay.Mcp;
using Kododo.MonitorWay.Otlp;
using System.Net;
using Kododo.Way.Shell;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddMonitorWay(x =>
{
    x.AddMemoryBuffer(m =>
    {
        m.Pool(LogLevel.Error, LogLevel.Critical).KeepEntries(2_000).KeepFor(TimeSpan.FromDays(1));
        m.Pool(LogLevel.Warning).KeepEntries(5_000);
        m.DefaultPool.MaxEntries = 20_000;
    });
    // The demo writes today's logs to logs/shop-*.clef and logs/shop-*.log, and a week of history to
    // logs/history.clef and logs/history.log. CLEF is detected; the text files use a custom template.
    x.AddFiles("logs/*.clef");
    x.AddFiles("logs/*.log", f => f.UseTemplate(TextFileLoggerProvider.Template));
    x.AddUi();
});

// The demo is public; a real application should call RequireAuthorization() on UseMonitorWay() instead.
builder.Services.AddWayShell(o => o.AllowAnonymous = true);

// MonitorWay is one more exporter of the OpenTelemetry SDK.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("shop"))
    .WithTracing(t => t.AddSource(Shop.SourceName).AddAspNetCoreInstrumentation().AddMonitorWay())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(Shop.SourceName)
        .AddView("shop.order.total", new ExplicitBucketHistogramConfiguration { Boundaries = [50, 100, 200, 300, 500, 750, 1000] })
        .AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());

var logs = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Logging.AddProvider(new ClefFileLoggerProvider(logs));
builder.Logging.AddProvider(new TextFileLoggerProvider(logs));
builder.Services.AddSingleton<Warehouse>();
builder.Services.AddSingleton<Shop>();
builder.Services.AddHostedService<ShopSimulator>();
builder.Services.AddHostedService<HistoryGenerator>();
builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();

var app = builder.Build();

var pathBase = app.Configuration["PathBase"] ?? "";
app.UsePathBase(pathBase);

app.MapGet("/", () => Results.Redirect($"{pathBase}/monitor/"));

// The shop the simulator sends its customers to.
app.MapPost("/shop/orders", async (string customer, Shop shop, CancellationToken cancellationToken) =>
    Results.StatusCode(await shop.PlaceOrderAsync(customer, cancellationToken)));
app.MapGet("/shop/products/{slug}", async (string slug, Shop shop, CancellationToken cancellationToken) =>
{
    await shop.BrowseAsync(slug, cancellationToken);
    return Results.Ok(new { slug });
});

app.UseMonitorWay();
app.MapMonitorWayMcp();

// The warehouse sends its telemetry here over OTLP. The demo is public, so the receiver takes only what comes from the
// machine itself; a real application would call RequireAuthorization() instead.
app.Use((context, next) =>
    context.Request.Path.StartsWithSegments("/monitor/otlp") && context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address)
        ? Results.StatusCode(StatusCodes.Status403Forbidden).ExecuteAsync(context)
        : next(context));
app.MapMonitorWayOtlp().AllowAnonymous();

app.Run();
