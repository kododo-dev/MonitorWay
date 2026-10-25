using System.ComponentModel;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Sources;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

/// <summary>
/// Categories of a source with their entry counts, most frequent first. For files the counts cover the indexed part.
/// </summary>
[Description("Lists the categories (logger names) of a source with their entry counts, most frequent first. Use them in the query's categories and excludedCategories.")]
internal sealed record GetCategories(
    [property: Description("Id of a source from the list of sources; several ids joined with commas read those sources together, ordered by time.")] string SourceId,
    [property: Description("At most this many categories.")] int Limit = GetCategories.DefaultLimit) : IRequest<CategoryCount[]>
{
    public const int DefaultLimit = 500;
}

internal sealed record CategoryCount(string Name, long Count);

internal sealed class GetCategoriesHandler(LogSources sources) : IRequestHandler<GetCategories, CategoryCount[]>
{
    public Task<CategoryCount[]> HandleAsync(GetCategories request, CancellationToken cancellationToken)
    {
        var counts = sources.Find(request.SourceId) switch
        {
            MemorySource memory => memory.Buffer.GetCategoryCounts(),
            FileLogSource file => file.GetCategoryCounts(),
            MergedSource merged => merged.GetCategoryCounts(),
            _ => [],
        };
        return Task.FromResult(counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(Math.Clamp(request.Limit, 1, 5_000))
            .Select(c => new CategoryCount(c.Key, c.Value))
            .ToArray());
    }
}
