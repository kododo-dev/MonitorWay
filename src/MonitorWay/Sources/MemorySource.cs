using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;

namespace Kododo.MonitorWay.Sources;

/// <summary>The in-memory buffer as a log source.</summary>
internal sealed class MemorySource(MemoryLogBuffer buffer) : ILogSource
{
    public const string SourceId = "memory";

    public MemoryLogBuffer Buffer => buffer;

    public string Id => SourceId;
    public string Name => "Memory";
    public string Kind => "memory";
    public long Version => 0;

    public ReadResult ReadBefore(long? beforeId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        var entries = buffer.ReadBefore(beforeId, count, matcher, out var hasMore);
        return new ReadResult(entries, hasMore);
    }

    public ReadResult ReadAfter(long afterId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        var entries = buffer.ReadAfter(afterId, count, matcher, out var hasMore);
        return new ReadResult(entries, hasMore);
    }

    public LogEntry? Find(long id) => buffer.Find(id);
}
