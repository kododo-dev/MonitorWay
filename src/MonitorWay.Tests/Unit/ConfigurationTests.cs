using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class ConfigurationTests
{
    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("64KB", 64 * 1024)]
    [InlineData("128MB", 128L * 1024 * 1024)]
    [InlineData("1.5 GB", 3L * 512 * 1024 * 1024)]
    [InlineData("10b", 10)]
    public void ParseSize_AcceptsUnits(string value, long expected)
    {
        Assert.Equal(expected, MemoryBufferConfiguration.ParseSize(value, "test"));
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("10TB")]
    [InlineData("MB")]
    public void ParseSize_RejectsInvalidValues(string value)
    {
        Assert.Throws<InvalidOperationException>(() => MemoryBufferConfiguration.ParseSize(value, "test"));
    }

    [Fact]
    public void Bind_ReadsPoolsAndLimits()
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:MaxTotalBytes"] = "128MB",
                ["Memory:DefaultPool:MaxEntries"] = "1000",
                ["Memory:Pools:0:Levels:0"] = "Error",
                ["Memory:Pools:0:Levels:1"] = "critical",
                ["Memory:Pools:0:MaxEntries"] = "5000",
                ["Memory:Pools:0:MaxAge"] = "7.00:00:00",
                ["Memory:Pools:1:Levels:0"] = "Debug",
                ["Memory:Pools:1:MaxBytes"] = "32MB",
            })
            .Build()
            .GetSection("Memory");

        var options = new MemoryBufferOptions();
        MemoryBufferConfiguration.Bind(section, options);

        Assert.Equal(128L * 1024 * 1024, options.MaxTotalBytes);
        Assert.Equal(1000, options.DefaultPool.MaxEntries);
        Assert.Equal(2, options.Pools.Count);
        Assert.Equal([LogLevel.Error, LogLevel.Critical], options.Pools[0].Levels);
        Assert.Equal(5000, options.Pools[0].MaxEntries);
        Assert.Equal(TimeSpan.FromDays(7), options.Pools[0].MaxAge);
        Assert.Equal([LogLevel.Debug], options.Pools[1].Levels);
        Assert.Equal(32L * 1024 * 1024, options.Pools[1].MaxBytes);
    }

    [Fact]
    public void Bind_RejectsUnknownLevel()
    {
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Memory:Pools:0:Levels:0"] = "Fatal" })
            .Build()
            .GetSection("Memory");

        Assert.Throws<InvalidOperationException>(() => MemoryBufferConfiguration.Bind(section, new MemoryBufferOptions()));
    }
}
