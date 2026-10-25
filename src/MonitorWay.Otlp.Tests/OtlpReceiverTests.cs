using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Otlp;
using Kododo.MonitorWay.UI;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Kododo.MonitorWay.Otlp.Tests;

/// <summary>Another service sends its telemetry with the OTLP exporter of the OpenTelemetry SDK, as a real one would.</summary>
public sealed class OtlpReceiverTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TracesFromTheExporter_AreStoredWithTheirService_AndTheirExceptionsAreErrors()
    {
        await using var app = await StartAsync();
        using var source = new ActivitySource($"warehouse.{Guid.NewGuid():N}");
        using (var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .ConfigureResource(r => r.AddService("warehouse"))
            .AddOtlpExporter(o => Exporter(o, app, "traces"))
            .Build())
        {
            using (var request = source.StartActivity("POST /reserve", ActivityKind.Server)!)
            {
                request.SetTag("http.route", "/reserve");
                using var query = source.StartActivity("UPDATE stock", ActivityKind.Client)!;
                query.SetStatus(ActivityStatusCode.Error, "deadlock");
                query.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    ["exception.type"] = "System.Data.DataException",
                    ["exception.message"] = "Deadlock victim",
                    ["exception.stacktrace"] = "System.Data.DataException: Deadlock victim\n   at Warehouse.Stock.Reserve() in Stock.cs:line 9",
                }));
            }
            Assert.True(tracing.ForceFlush());
        }

        var traces = await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "service:warehouse" } });
        var row = Assert.Single(traces.Traces);
        Assert.Equal("POST /reserve", row.Name);
        Assert.Equal(["warehouse"], row.Services);
        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId = row.TraceId });
        Assert.Equal(2, trace.Spans.Length);
        Assert.Equal("Error", trace.Spans.Single(s => s.Name == "UPDATE stock").Status);
        Assert.Equal(source.Name, trace.Spans[0].Scope);

        var errors = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { query = new { text = "service:warehouse" } });
        var error = Assert.Single(errors.Errors);
        Assert.Equal("System.Data.DataException", error.Type);
        Assert.Equal(["POST /reserve"], error.Endpoints);

        var services = await PostAsync<ServiceDto[]>(app, "GetServices", new { });
        Assert.Contains(services, s => s.Name == "warehouse" && s.Signals.Contains("traces"));
    }

    [Fact]
    public async Task SpansOfOneTraceFromTwoServices_AreOneTrace()
    {
        var shopSource = $"shop.{Guid.NewGuid():N}";
        await using var app = await StartAsync(tracing: shopSource);
        using var shop = new ActivitySource(shopSource);
        using var warehouse = new ActivitySource($"warehouse.{Guid.NewGuid():N}");
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(warehouse.Name)
            .ConfigureResource(r => r.AddService("warehouse"))
            .AddOtlpExporter(o => Exporter(o, app, "traces"))
            .Build();

        string traceId;
        using (var order = shop.StartActivity("POST /orders", ActivityKind.Server)!)
        {
            traceId = order.TraceId.ToHexString();
            using var call = shop.StartActivity("POST warehouse", ActivityKind.Client)!;
            // The warehouse receives the call's context in the request headers, as from another process.
            var remote = new ActivityContext(call.TraceId, call.SpanId, call.ActivityTraceFlags, isRemote: true);
            using (warehouse.StartActivity("POST /reserve", ActivityKind.Server, remote))
            {
            }
            Assert.True(tracing.ForceFlush());
        }

        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId });
        Assert.Equal(["POST /orders", "POST warehouse", "POST /reserve"], trace.Spans.Select(s => s.Name));
        var row = Assert.Single((await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = $"trace:{traceId}" } })).Traces);
        Assert.Equal(["shop", "warehouse"], row.Services.Order());
        Assert.Equal("POST /orders", row.Name);
    }

    [Fact]
    public async Task LogsFromTheExporter_AreStored_WithTheirTemplateAndException()
    {
        await using var app = await StartAsync();
        using (var loggers = LoggerFactory.Create(l =>
        {
            l.SetMinimumLevel(LogLevel.Debug);
            l.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("warehouse"));
                o.IncludeFormattedMessage = true;
                o.AddOtlpExporter((e, processor) =>
                {
                    Exporter(e, app, "logs");
                    processor.ExportProcessorType = ExportProcessorType.Simple;
                });
            });
        }))
        {
            var logger = loggers.CreateLogger("Warehouse.Stock");
            logger.LogInformation("Reserved {Quantity} x {Product}", 3, "dock");
            logger.LogError(new InvalidOperationException("Shelf empty"), "Could not reserve {Product}", "cable");
        }

        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = "memory", query = new { text = "service:warehouse" } });
        Assert.Equal(["Reserved 3 x dock", "Could not reserve cable"], page.Entries.Select(e => e.Message));
        Assert.Equal(["Information", "Error"], page.Entries.Select(e => e.Level));
        Assert.All(page.Entries, e => Assert.Equal("Warehouse.Stock", e.Category));

        var entry = await PostAsync<EntryDetailDto>(app, "GetEntry", new { sourceId = "memory", id = page.Entries[0].Id });
        Assert.Contains(entry.Properties, p => p.Key == "Product" && p.Value == "dock");
        Assert.Equal("Reserved {Quantity} x {Product}", entry.MessageTemplate);

        var errors = await PostAsync<ErrorsPageDto>(app, "GetErrors", new { query = new { text = "type:InvalidOperation" } });
        Assert.Equal(["warehouse"], Assert.Single(errors.Errors).Services);
    }

    [Fact]
    public async Task MetricsFromTheExporter_RunningTotalsBecomeIncreases()
    {
        await using var app = await StartAsync();
        using var meter = new Meter($"warehouse.{Guid.NewGuid():N}");
        var picked = meter.CreateCounter<long>("warehouse.items.picked");
        var duration = meter.CreateHistogram<double>("warehouse.pick.duration", "s");
        using (var metrics = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .ConfigureResource(r => r.AddService("warehouse"))
            // The exporter's default: running totals since the start of the process.
            .AddOtlpExporter((e, reader) =>
            {
                Exporter(e, app, "metrics");
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            })
            .Build())
        {
            picked.Add(5);
            duration.Record(0.2);
            Assert.True(metrics.ForceFlush());
            picked.Add(2);
            duration.Record(0.4);
            duration.Record(0.6);
            Assert.True(metrics.ForceFlush());
        }

        var list = await PostAsync<InstrumentDto[]>(app, "GetMetrics", new { });
        var counter = Assert.Single(list, m => m.Name == "warehouse.items.picked");
        Assert.Equal("Counter", counter.Kind);
        Assert.Contains(counter.Attributes, a => a.Key == "service.name");

        // The first total has nothing before it to compare with; what came after it is kept as an increase.
        var increase = await PostAsync<SeriesPageDto>(app, "GetMetricSeries", new { key = counter.Key, stat = "sum" });
        Assert.Equal(2, Assert.Single(increase.Series).Total);
        var histogram = Assert.Single(list, m => m.Name == "warehouse.pick.duration");
        var count = await PostAsync<SeriesPageDto>(app, "GetMetricSeries", new { key = histogram.Key, stat = "count" });
        Assert.Equal(2, Assert.Single(count.Series).Total);
    }

    [Fact]
    public async Task Json_WithHexIds_IsRead()
    {
        await using var app = await StartAsync();
        var now = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = $$$"""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"billing"}}]},
              "scopeSpans":[{"scope":{"name":"billing.api"},"spans":[{
                "traceId":"5b8efff798038103d269b633813fc60c","spanId":"eee19b7ec3c1b174","name":"GET /invoices",
                "kind":2,"startTimeUnixNano":"{{{now}}}","endTimeUnixNano":"{{{now}}}",
                "attributes":[{"key":"http.response.status_code","value":{"intValue":"200"}}],"status":{}}]}]}]}
            """;

        var response = await app.GetTestClient().PostAsync("/monitor/otlp/v1/traces", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var trace = await PostAsync<TraceDetailDto>(app, "GetTrace", new { traceId = "5b8efff798038103d269b633813fc60c" });
        Assert.Equal("GET /invoices", Assert.Single(trace.Spans).Name);
        var row = Assert.Single((await PostAsync<TracesPageDto>(app, "GetTraces", new { query = new { text = "service:billing" } })).Traces);
        Assert.Equal("200", row.StatusCode);
    }

    [Fact]
    public async Task GzipProtobuf_IsRead()
    {
        await using var app = await StartAsync();
        var message = new OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest
        {
            ResourceLogs =
            {
                new OpenTelemetry.Proto.Logs.V1.ResourceLogs
                {
                    ScopeLogs =
                    {
                        new OpenTelemetry.Proto.Logs.V1.ScopeLogs
                        {
                            LogRecords =
                            {
                                new OpenTelemetry.Proto.Logs.V1.LogRecord
                                {
                                    TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                                    SeverityText = "WARN",
                                    Body = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "compressed" },
                                },
                            },
                        },
                    },
                },
            },
        };
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Google.Protobuf.MessageExtensions.ToByteArray(message));
        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");

        var response = await app.GetTestClient().PostAsync("/monitor/otlp/v1/logs", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await PostAsync<EntriesPageDto>(app, "GetEntries", new { sourceId = "memory", query = new { text = "service:unknown_service" } });
        var entry = Assert.Single(page.Entries);
        Assert.Equal("compressed", entry.Message);
        Assert.Equal("Warning", entry.Level);
    }

    [Theory]
    [InlineData("text/plain", "x", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/x-protobuf", "not protobuf at all", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"zz\"}]}]}]}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "[1,", HttpStatusCode.BadRequest)]
    public async Task InvalidRequests_AreRefused(string contentType, string body, HttpStatusCode expected)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsync("/monitor/otlp/v1/traces", new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_TheReceiverIsClosedUntilDecided()
    {
        await using var closed = await StartAsync("Production");
        await using var open = await StartAsync("Production", allowAnonymous: true);
        var empty = new ByteArrayContent([]) { Headers = { ContentType = new("application/x-protobuf") } };

        Assert.Equal(HttpStatusCode.Forbidden, (await closed.GetTestClient().PostAsync("/monitor/otlp/v1/traces", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await open.GetTestClient().PostAsync("/monitor/otlp/v1/traces", empty)).StatusCode);
    }

    private static void Exporter(OtlpExporterOptions options, WebApplication app, string signal)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri($"http://localhost/monitor/otlp/v1/{signal}");
        options.ExportProcessorType = ExportProcessorType.Simple;
        options.HttpClientFactory = () => new HttpClient(new Synchronous(app.GetTestServer().CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
    }

    /// <summary>
    /// The exporter sends with the synchronous <see cref="HttpClient.Send(HttpRequestMessage)"/>, which the test server's
    /// handler does not implement; over a network it is a plain request.
    /// </summary>
    private sealed class Synchronous(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }

    private static async Task<WebApplication> StartAsync(string environment = "Development", bool allowAnonymous = false, string? tracing = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddMonitorWay(x =>
        {
            // Stored as soon as a root ends: the parts of a trace from two services are then stored one after the other and
            // joined as late spans are.
            x.ConfigureTraces(t => t.CompletionDelay = TimeSpan.Zero);
            x.AddUi();
        });
        builder.Services.AddWayShell(o => o.AllowAnonymous = true);
        if (tracing is not null)
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("shop"))
                .WithTracing(t => t.AddSource(tracing).AddMonitorWay());
        }
        var app = builder.Build();
        var otlp = app.MapMonitorWayOtlp();
        if (allowAnonymous)
            otlp.AllowAnonymous();
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

    private sealed record TraceRowDto(string TraceId, string Name, string[] Services, string? StatusCode);
    private sealed record TracesPageDto(TraceRowDto[] Traces);
    private sealed record SpanDto(string Name, string Status, string? Scope);
    private sealed record TraceDetailDto(SpanDto[] Spans);
    private sealed record ErrorRowDto(string Type, string[] Services, string[] Endpoints);
    private sealed record ErrorsPageDto(ErrorRowDto[] Errors);
    private sealed record ServiceDto(string Name, string[] Signals);
    private sealed record EntryRowDto(long Id, string Level, string? Category, string Message);
    private sealed record EntriesPageDto(EntryRowDto[] Entries);
    private sealed record PropertyDto(string Key, string? Value);
    private sealed record EntryDetailDto(string? MessageTemplate, PropertyDto[] Properties);
    private sealed record AttributeDto(string Key);
    private sealed record InstrumentDto(string Key, string Name, string Kind, AttributeDto[] Attributes);
    private sealed record SeriesDto(double? Total);
    private sealed record SeriesPageDto(SeriesDto[] Series);
}
