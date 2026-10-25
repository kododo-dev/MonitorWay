using System.ComponentModel;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

/// <summary>
/// A window of entries. Without <see cref="Before"/> and <see cref="After"/> it returns the newest ones.
/// </summary>
[Description("Reads log entries of a source, oldest first in the result. Without before and after it returns the newest ones. " +
             "To page back, pass the cursor, or else the id of the first entry, as before. When a sparse search stops early, " +
             "entries may be few or none with hasMore true: continue with the cursor. Entries show the message only; " +
             "read a single entry for its exception and properties.")]
internal sealed record GetEntries(
    [property: Description("Id of a source from the list of sources; several ids joined with commas read those sources together, ordered by time.")] string SourceId,
    [property: Description("Return entries older than this id or cursor.")] long? Before = null,
    [property: Description("Return entries newer than this id or cursor.")] long? After = null,
    [property: Description("How many entries to return, at most 1000. Keep it small, e.g. 30, and narrow the query instead.")] int Count = GetEntries.DefaultCount,
    [property: Description("Filters; all that are set must match.")] Query? Query = null) : IRequest<EntriesPage>
{
    public const int DefaultCount = 200;
    public const int MaxCount = 1000;
}

internal sealed class GetEntriesHandler(LogSources sources) : IRequestHandler<GetEntries, EntriesPage>
{
    public Task<EntriesPage> HandleAsync(GetEntries request, CancellationToken cancellationToken)
    {
        if (sources.Find(request.SourceId) is not { } source)
            return Task.FromResult(new EntriesPage([], false, $"Unknown source '{request.SourceId}'."));

        LogQueryMatcher matcher;
        try
        {
            matcher = request.Query is null ? LogQueryMatcher.All : LogQueryMatcher.Create(request.Query.ToLogQuery());
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(new EntriesPage([], false, e.Message));
        }

        var count = request.Count is > 0 and <= GetEntries.MaxCount ? request.Count : GetEntries.DefaultCount;
        var result = request.After is { } after
            ? source.ReadAfter(after, count, matcher, cancellationToken)
            : source.ReadBefore(request.Before, count, matcher, cancellationToken);

        return Task.FromResult(new EntriesPage(
            result.Entries.Select(EntryRow.Of(source)).ToArray(),
            result.HasMore,
            GetAroundHandler.ErrorOf(source),
            result.Cursor,
            source.Version));
    }
}
