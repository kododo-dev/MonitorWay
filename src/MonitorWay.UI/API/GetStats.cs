using System.ComponentModel;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.UI.API;

/// <summary>Entry counts per level and the state of the source: retention pools or the file and its index.</summary>
[Description("Counts the entries of a source per level and describes it: file size, format and indexing progress, or the memory buffer's retention.")]
internal sealed record GetStats([property: Description("Id of a source from the list of sources; several ids joined with commas read those sources together, ordered by time.")] string SourceId) : IRequest<Stats?>;

internal sealed class GetStatsHandler(LogSources sources, TimeProvider time) : IRequestHandler<GetStats, Stats?>
{
    public Task<Stats?> HandleAsync(GetStats request, CancellationToken cancellationToken) =>
        Task.FromResult(sources.Find(request.SourceId) switch
        {
            MemorySource memory => MemoryStats(memory),
            FileLogSource file => FileStats(file.GetStats()),
            MergedSource merged => FileStats(merged.GetStats()),
            _ => null,
        });

    private static Stats MemoryStats(MemorySource source)
    {
        var stats = source.Buffer.GetStats();
        var levels = new List<LevelStats>();
        for (var pool = 0; pool < stats.Pools.Count; pool++)
        {
            foreach (var level in stats.Pools[pool].Levels)
                levels.Add(new LevelStats(level.ToString(), stats.LevelCounts[(int)level], pool));
        }

        return new Stats(
            levels.OrderBy(l => Enum.Parse<LogLevel>(l.Level)).ToArray(),
            stats.Pools.Select(Pool.From).ToArray(),
            stats.TotalBytes, stats.MaxTotalBytes, stats.LastId, stats.Now);
    }

    private Stats FileStats(FileStats stats)
    {
        var levels = Enumerable.Range((int)LogLevel.Trace, (int)LogLevel.Critical + 1)
            .Select(level => new LevelStats(((LogLevel)level).ToString(), stats.LevelCounts[level], -1))
            .ToArray();

        return new Stats(levels, [], 0, 0, 0, time.GetUtcNow(), stats.Version,
            new FileDetails(stats.Path, stats.Length, stats.IndexedEnd, stats.Indexing, stats.Format, stats.Error));
    }
}
