using System.Diagnostics;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

/// <summary>Logs reach MonitorWay through the OpenTelemetry SDK, as through any other exporter.</summary>
public class LogExporterTests
{
    [Fact]
    public void CapturesTemplatePropertiesAndScopes()
    {
        using var host = CreateHost();
        var logger = host.Services.GetRequiredService<ILogger<LogExporterTests>>();

        using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "r-1" }))
        using (logger.BeginScope("plain scope"))
            logger.LogInformation(new EventId(7, "Placed"), "Order {OrderId} placed for {Amount}", 42, 9.5m);

        var entry = Single(host);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(typeof(LogExporterTests).FullName, entry.Category);
        Assert.Equal("Order 42 placed for 9.5", entry.Message);
        Assert.Equal("Order {OrderId} placed for {Amount}", entry.MessageTemplate);
        Assert.Contains(new("OrderId", "42"), entry.Properties);
        Assert.Contains(new("Amount", "9.5"), entry.Properties);
        Assert.Contains(new("EventId", "7"), entry.Properties);
        Assert.Contains(new("EventName", "Placed"), entry.Properties);
        Assert.Contains(new("RequestId", "r-1"), entry.Properties);
        Assert.Contains(new("Scope", "plain scope"), entry.Properties);
    }

    [Fact]
    public void MessageWithoutTemplateValues_HasNoSeparateTemplate()
    {
        using var host = CreateHost();
        host.Services.GetRequiredService<ILogger<LogExporterTests>>().LogWarning("plain text");

        var entry = Single(host);
        Assert.Equal("plain text", entry.Message);
        Assert.Null(entry.MessageTemplate);
    }

    [Fact]
    public void CapturesExceptionActivityAndService()
    {
        using var host = CreateHost();
        var logger = host.Services.GetRequiredService<ILogger<LogExporterTests>>();

        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        logger.LogError(new InvalidOperationException("boom"), "Failed");

        var entry = Single(host);
        Assert.Contains("System.InvalidOperationException: boom", entry.Exception);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Equal(activity.TraceId.ToHexString(), entry.TraceId);
        Assert.Equal(activity.SpanId.ToHexString(), entry.SpanId);
        Assert.Equal("shop", entry.ServiceName);
    }

    [Fact]
    public void IgnoresOwnCategories()
    {
        using var host = CreateHost();

        var loggers = host.Services.GetRequiredService<ILoggerFactory>();
        loggers.CreateLogger("Kododo.MonitorWay.UI.API.GetEntriesHandler").LogWarning("internal");
        loggers.CreateLogger("Kododo.MonitorWay.Demo.Web.Program").LogWarning("application");

        Assert.Equal("application", Single(host).Message);
    }

    [Fact]
    public void OpenTelemetryProviderLevels_ApplyAsUsual()
    {
        using var host = CreateHost(new Dictionary<string, string?> { ["Logging:OpenTelemetry:LogLevel:Default"] = "Warning" });
        var logger = host.Services.GetRequiredService<ILogger<LogExporterTests>>();

        logger.LogInformation("skipped");
        logger.LogWarning("kept");

        Assert.Equal("kept", Single(host).Message);
    }

    [Fact]
    public void WorksNextToAnotherExporter()
    {
        var other = new List<LogRecord>();
        using var host = CreateHost(configure: l => l.AddInMemoryExporter(other));

        host.Services.GetRequiredService<ILogger<LogExporterTests>>().LogWarning("both");

        Assert.Equal("both", Single(host).Message);
        Assert.Single(other);
    }

    [Fact]
    public void AddMemoryBuffer_ReadsConfiguration()
    {
        using var host = CreateHost(new Dictionary<string, string?>
        {
            ["MonitorWay:Memory:Pools:0:Levels:0"] = "Warning",
            ["MonitorWay:Memory:Pools:0:MaxEntries"] = "2",
        });
        var logger = host.Services.GetRequiredService<ILogger<LogExporterTests>>();

        for (var i = 0; i < 5; i++)
            logger.LogWarning("w{Index}", i);

        var entries = host.Services.GetRequiredService<MemoryLogBuffer>().ReadBefore(null, 10, LogQueryMatcher.All, out _);
        Assert.Equal(["w3", "w4"], entries.Select(e => e.Message));
    }

    [Fact]
    public void AddMonitorWay_InvalidPoolsFailAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.AddMonitorWay(x => x.AddMemoryBuffer(m =>
        {
            m.Pool(LogLevel.Error);
            m.Pool(LogLevel.Error);
        })));
    }

    [Fact]
    public void ExporterWithoutAddMonitorWay_FailsWithAClearMessage()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddOpenTelemetry().WithLogging(l => l.AddMonitorWay());

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = builder.Build();
            host.Services.GetRequiredService<ILoggerFactory>();
        });
        Assert.Contains("builder.AddMonitorWay()", error.Message);
    }

    private static IHost CreateHost(
        Dictionary<string, string?>? configuration = null, Action<LoggerProviderBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        if (configuration is not null)
            builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.AddMonitorWay();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("shop"))
            .WithLogging(l =>
            {
                l.AddMonitorWay();
                configure?.Invoke(l);
            });
        return builder.Build();
    }

    private static Core.Model.LogEntry Single(IHost host) =>
        Assert.Single(host.Services.GetRequiredService<MemoryLogBuffer>().ReadBefore(null, 10, LogQueryMatcher.All, out _));
}
