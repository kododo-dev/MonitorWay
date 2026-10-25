using System.Diagnostics;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

/// <summary>Spans reach MonitorWay through the OpenTelemetry SDK, as through any other exporter.</summary>
public class TraceExporterTests
{
    private static readonly ActivitySource Source = new(nameof(TraceExporterTests));

    [Fact]
    public void CapturesSpansOfATrace()
    {
        using var host = CreateHost();

        string traceId;
        using (var root = Source.StartActivity("PlaceOrder", ActivityKind.Server)!)
        {
            traceId = root.TraceId.ToHexString();
            root.SetTag("http.route", "/orders");
            root.SetTag("order.items", new[] { 1, 2 });
            using (var query = Source.StartActivity("SELECT orders", ActivityKind.Client)!)
            {
                query.SetTag("db.system", "postgresql");
                query.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection { ["attempt"] = 2 }));
            }
        }

        var trace = Single(host);
        Assert.Equal(traceId, trace.TraceId);
        Assert.True(trace.IsComplete);
        Assert.Equal(2, trace.Spans.Count);
        Assert.Equal("PlaceOrder", trace.Root.Name);
        Assert.Equal(TraceSpanKind.Server, trace.Root.Kind);
        Assert.Null(trace.Root.ParentSpanId);
        Assert.Equal("shop", trace.Root.ServiceName);
        Assert.Equal(nameof(TraceExporterTests), trace.Root.ScopeName);
        Assert.Equal("/orders", trace.Root.Attribute("http.route"));
        Assert.Equal("1, 2", trace.Root.Attribute("order.items"));

        var child = trace.Spans.Single(s => s.Name == "SELECT orders");
        Assert.Equal(trace.Root.SpanId, child.ParentSpanId);
        Assert.Equal(TraceSpanKind.Client, child.Kind);
        Assert.Equal("postgresql", child.Attribute("db.system"));
        var e = Assert.Single(child.Events);
        Assert.Equal("retry", e.Name);
        Assert.Contains(new("attempt", "2"), e.Attributes);
    }

    [Fact]
    public void CapturesStatusAndExceptions()
    {
        using var host = CreateHost();

        using (var root = Source.StartActivity("Pay")!)
        {
            root.SetStatus(ActivityStatusCode.Error, "card declined");
            root.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = "System.TimeoutException",
                ["exception.message"] = "no answer",
            }));
        }

        var trace = Single(host);
        Assert.True(trace.HasError);
        Assert.Equal(TraceSpanStatus.Error, trace.Root.Status);
        Assert.Equal("card declined", trace.Root.StatusDescription);
        Assert.Equal("exception", Assert.Single(trace.Root.Events).Name);
    }

    [Fact]
    public void StatusAsTag_IsReadAsStatus()
    {
        using var host = CreateHost();

        using (var root = Source.StartActivity("Legacy")!)
        {
            root.SetTag("otel.status_code", "ERROR");
            root.SetTag("otel.status_description", "failed");
        }

        var root2 = Single(host).Root;
        Assert.Equal(TraceSpanStatus.Error, root2.Status);
        Assert.Equal("failed", root2.StatusDescription);
        Assert.DoesNotContain(root2.Attributes, a => a.Key.StartsWith("otel.status", StringComparison.Ordinal));
    }

    [Fact]
    public void SpanWithRemoteParent_IsTheRootOfItsPart()
    {
        using var host = CreateHost();

        var remote = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        using (Source.StartActivity("Consume", ActivityKind.Consumer, remote))
        {
        }

        var trace = Single(host);
        Assert.True(trace.Root.HasRemoteParent);
        Assert.Equal(remote.SpanId.ToHexString(), trace.Root.ParentSpanId);
        Assert.Equal(remote.TraceId.ToHexString(), trace.TraceId);
    }

    [Fact]
    public void UnsampledSpans_AreNotKept()
    {
        using var host = CreateHost(t => t.SetSampler(new AlwaysOffSampler()));

        using (Source.StartActivity("Skipped"))
        {
        }

        Assert.Empty(Read(host));
    }

    [Fact]
    public void WorksNextToAnotherExporter()
    {
        var other = new List<Activity>();
        using var host = CreateHost(t => t.AddInMemoryExporter(other));

        using (Source.StartActivity("Both"))
        {
        }

        Assert.Equal("Both", Single(host).Root.Name);
        Assert.Single(other);
    }

    [Fact]
    public void ConfigureTraces_ReadsConfiguration()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MonitorWay:Traces:MaxTotalBytes"] = "8MB",
            ["MonitorWay:Traces:SlowThreshold"] = "00:00:00.250",
            ["MonitorWay:Traces:Errors:MaxTraces"] = "7",
            ["MonitorWay:Traces:Other:MaxAge"] = "01:00:00",
        });
        builder.AddMonitorWay(x => x.ConfigureTraces(t => t.Slow.KeepTraces(3)));
        using var host = builder.Build();

        var store = host.Services.GetRequiredService<MemoryTraceStore>();
        Assert.Equal(8L * 1024 * 1024, store.MaxTotalBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(250), store.SlowThreshold);
        var pools = store.Pools.ToDictionary(p => p.Name);
        Assert.Equal(7, pools["errors"].MaxTraces);
        Assert.Equal(3, pools["slow"].MaxTraces);
        Assert.Equal(TimeSpan.FromHours(1), pools["other"].MaxAge);
    }

    [Fact]
    public void ExporterWithoutAddMonitorWay_FailsWithAClearMessage()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddMonitorWay());

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = builder.Build();
            host.Services.GetRequiredService<TracerProvider>();
        });
        Assert.Contains("builder.AddMonitorWay()", error.Message);
    }

    private static IHost CreateHost(Action<TracerProviderBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        // Stored as soon as the root ends, so the test needs no clock.
        builder.AddMonitorWay(x => x.ConfigureTraces(t => t.CompletionDelay = TimeSpan.Zero));
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("shop"))
            .WithTracing(t =>
            {
                t.AddSource(nameof(TraceExporterTests));
                configure?.Invoke(t);
                t.AddMonitorWay();
            });
        var host = builder.Build();
        // The provider, and with it the listener of the source, is created when it is first resolved.
        host.Services.GetRequiredService<TracerProvider>();
        return host;
    }

    private static IReadOnlyList<TraceEntry> Read(IHost host) =>
        host.Services.GetRequiredService<MemoryTraceStore>().ReadBefore(null, 10, TraceQueryMatcher.All, out _);

    private static TraceEntry Single(IHost host) => Assert.Single(Read(host));
}
