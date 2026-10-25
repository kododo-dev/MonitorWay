using BenchmarkDotNet.Attributes;
using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Benchmarks;

/// <summary>What logging costs the application with the memory buffer in place.</summary>
[MemoryDiagnoser]
public class LoggingBenchmarks
{
    private IHost _host = null!;
    private ILogger _logger = null!;
    private MemoryLogBuffer _buffer = null!;
    private PendingEntry _entry;

    [GlobalSetup]
    public void Setup()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.AddMonitorWay(x => x.AddMemoryBuffer(m => m.DefaultPool.MaxEntries = 100_000));
        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<LoggingBenchmarks>>();
        _buffer = _host.Services.GetRequiredService<MemoryLogBuffer>();
        _entry = EntryCapture.Create(DateTimeOffset.UtcNow, LogLevel.Information, "Shop.Orders", "Order 42 placed",
            "Order {OrderId} placed", null, [new("OrderId", "42")], null, null);
    }

    [GlobalCleanup]
    public void Cleanup() => _host.Dispose();

    [Benchmark(Description = "ILogger.LogInformation with 2 properties")]
    public void LogThroughILogger() => _logger.LogInformation("Order {OrderId} placed for {Customer}", 42, "ann");

    [Benchmark(Description = "Buffer append (entry already captured)")]
    public void AppendToBuffer() => _buffer.Append(_entry);
}
