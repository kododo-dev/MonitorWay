using Kododo.MonitorWay.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Kododo.MonitorWay.Tests.Unit;

internal static class TestBuffer
{
    public static MemoryLogBuffer Create(Action<MemoryBufferOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new MemoryBufferOptions();
        configure?.Invoke(options);
        return new MemoryLogBuffer(options, time ?? new FakeTimeProvider());
    }

    public static PendingEntry Entry(LogLevel level, string message = "message", int size = 100, DateTimeOffset? timestamp = null) =>
        new(timestamp ?? DateTimeOffset.UtcNow, level, "Test.Category", message, null, null, [], null, null, size);

    public static void Append(this MemoryLogBuffer buffer, LogLevel level, int count, int size = 100, TimeProvider? time = null)
    {
        for (var i = 0; i < count; i++)
            buffer.Append(Entry(level, $"{level} {i}", size, time?.GetUtcNow()));
    }

    public static int CountOf(this MemoryLogBuffer buffer, LogLevel level) =>
        buffer.GetStats().LevelCounts[(int)level];
}
