using System.Diagnostics.Metrics;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

/// <summary>Metrics reach MonitorWay through the OpenTelemetry SDK, as through any other reader.</summary>
public class MetricExporterTests
{
    [Fact]
    public void CountersHistogramsAndGauges_AreKept()
    {
        using var meter = new Meter($"{nameof(MetricExporterTests)}.{Guid.NewGuid():N}");
        var orders = meter.CreateCounter<long>("shop.orders", "{order}", "Orders placed");
        var total = meter.CreateHistogram<double>("shop.order.total", "PLN");
        var open = meter.CreateUpDownCounter<int>("shop.carts.open");
        meter.CreateObservableGauge("shop.stock", () => 42);
        using var host = CreateHost(meter.Name);

        orders.Add(2, new KeyValuePair<string, object?>("result", "placed"));
        orders.Add(1, new KeyValuePair<string, object?>("result", "failed"));
        total.Record(20);
        total.Record(700);
        open.Add(3);
        open.Add(-1);
        Collect(host);
        orders.Add(4, new KeyValuePair<string, object?>("result", "placed"));
        Collect(host);

        var store = host.Services.GetRequiredService<MemoryMetricStore>();
        var instruments = store.List().ToDictionary(i => i.Instrument.Name);
        Assert.Equal(MetricKind.Counter, instruments["shop.orders"].Instrument.Kind);
        Assert.Equal("{order}", instruments["shop.orders"].Instrument.Unit);
        Assert.Equal("Orders placed", instruments["shop.orders"].Instrument.Description);
        Assert.Equal(MetricKind.Histogram, instruments["shop.order.total"].Instrument.Kind);
        Assert.Equal(MetricKind.UpDownCounter, instruments["shop.carts.open"].Instrument.Kind);
        Assert.Equal(MetricKind.Gauge, instruments["shop.stock"].Instrument.Kind);

        var now = DateTimeOffset.UtcNow;
        var byResult = store.Aggregate($"{meter.Name}/shop.orders", now.AddMinutes(-5), now, ["result"])!;
        // Deltas add up to the increase, not to the running totals.
        Assert.Equal(6, byResult.Groups.Single(g => g.Attributes[0].Value == "placed").Total.Sum);
        Assert.Equal(1, byResult.Groups.Single(g => g.Attributes[0].Value == "failed").Total.Sum);

        var orderTotal = Assert.Single(store.Aggregate($"{meter.Name}/shop.order.total", now.AddMinutes(-5), now, [])!.Groups).Total;
        Assert.Equal(2, orderTotal.Count);
        Assert.Equal(720, orderTotal.Sum);
        Assert.Equal(20, orderTotal.Min);
        Assert.Equal(700, orderTotal.Max);

        Assert.Equal(2, Assert.Single(store.Aggregate($"{meter.Name}/shop.carts.open", now.AddMinutes(-5), now, [])!.Groups).Total.Last);
        Assert.Equal(42, Assert.Single(store.Aggregate($"{meter.Name}/shop.stock", now.AddMinutes(-5), now, [])!.Groups).Total.Last);
    }

    [Fact]
    public void RequestsToTheUi_AreLeftOut()
    {
        using var meter = new Meter($"{nameof(MetricExporterTests)}.{Guid.NewGuid():N}");
        var requests = meter.CreateCounter<long>("http.server.requests");
        using var host = CreateHost(meter.Name);
        host.Services.GetRequiredService<IgnoredRequests>().Add("/monitor");

        requests.Add(1, new KeyValuePair<string, object?>("http.route", "/monitor/api/GetTraces"));
        requests.Add(1, new KeyValuePair<string, object?>("http.route", "/orders"));
        Collect(host);

        Assert.Equal(1, Assert.Single(host.Services.GetRequiredService<MemoryMetricStore>().List()).Series);
    }

    [Fact]
    public void ExporterWithoutAddMonitorWay_FailsWithAClearMessage()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMonitorWay());

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = builder.Build();
            host.Services.GetRequiredService<MeterProvider>();
        });
        Assert.Contains("builder.AddMonitorWay()", error.Message);
    }

    private static IHost CreateHost(string meter)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddMonitorWay();
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(meter).AddMonitorWay());
        var host = builder.Build();
        host.Services.GetRequiredService<MeterProvider>();
        return host;
    }

    private static void Collect(IHost host) =>
        Assert.True(host.Services.GetRequiredService<MeterProvider>().ForceFlush());
}
