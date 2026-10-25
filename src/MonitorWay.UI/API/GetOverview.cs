using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Sources;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

/// <summary>
/// Where entries, warnings and errors are along the whole source, for the overview bar next to the list.
/// Positions are in id space, or, for a merged source, Unix milliseconds (<see cref="OverviewDto.Time"/>); either way
/// a position from the bar can be passed to <see cref="GetAround"/>.
/// </summary>
internal sealed record GetOverview(string SourceId, int Buckets = GetOverview.DefaultBuckets) : IRequest<OverviewDto?>
{
    public const int DefaultBuckets = 120;
    public const int MaxBuckets = 1000;
}

/// <param name="Start">Id at the start of the source (inclusive).</param>
/// <param name="End">Id after its end (exclusive).</param>
/// <param name="Time">Positions are Unix milliseconds instead of ids.</param>
internal sealed record OverviewDto(long Start, long End, OverviewBucketDto[] Buckets, bool Time = false);

internal sealed record OverviewBucketDto(long Total, long Warnings, long Errors);

internal sealed class GetOverviewHandler(LogSources sources) : IRequestHandler<GetOverview, OverviewDto?>
{
    public Task<OverviewDto?> HandleAsync(GetOverview request, CancellationToken cancellationToken)
    {
        var buckets = Math.Clamp(request.Buckets, 1, GetOverview.MaxBuckets);
        var source = sources.Find(request.SourceId);
        var overview = source switch
        {
            MemorySource memory => memory.Buffer.GetOverview(buckets),
            FileLogSource file => file.GetOverview(buckets),
            MergedSource merged => merged.GetOverview(buckets),
            _ => null,
        };
        return Task.FromResult(overview is null
            ? null
            : new OverviewDto(overview.Start, overview.End,
                overview.Buckets.Select(b => new OverviewBucketDto(b.Total, b.Warnings, b.Errors)).ToArray(),
                source is MergedSource));
    }
}
