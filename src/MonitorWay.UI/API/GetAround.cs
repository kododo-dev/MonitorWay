using System.ComponentModel;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

/// <summary>
/// A window of entries centred on an entry (<see cref="Id"/>), on the first entry at or after a moment
/// (<see cref="Time"/>) or on the first entry at or after a position in id space (<see cref="Position"/>, from
/// the overview bar). Used for showing an entry in its context, jumping to a time and seeking in a large source.
/// </summary>
/// <remarks>
/// With <see cref="Time"/> or <see cref="Position"/>, the anchor is the first matching entry at or after it, or
/// the nearest matching entry before it when there is none. The query applies to the anchor search and to the window.
/// </remarks>
[Description("Reads the entries around one entry (id) or around a moment (time): what happened just before and after. " +
             "With an id and no query it shows the entry in its full context, every level and category.")]
internal sealed record GetAround(
    [property: Description("Id of a source from the list of sources; several ids joined with commas read those sources together, ordered by time.")] string SourceId,
    [property: Description("Centre on this entry.")] long? Id = null,
    [property: Description("Centre on the first matching entry at or after this moment (ISO 8601).")] DateTimeOffset? Time = null,
    [property: Description("Used by the UI's overview bar; leave it out.")] long? Position = null,
    [property: Description("How many entries in total, half on each side. Keep it small, e.g. 20.")] int Count = GetAround.DefaultCount,
    [property: Description("Filters for the anchor and the window; all that are set must match.")] Query? Query = null) : IRequest<AroundPage>
{
    public const int DefaultCount = 100;
}

internal sealed class GetAroundHandler(LogSources sources) : IRequestHandler<GetAround, AroundPage>
{
    public Task<AroundPage> HandleAsync(GetAround request, CancellationToken cancellationToken)
    {
        if (sources.Find(request.SourceId) is not { } source)
            return Task.FromResult(Empty($"Unknown source '{request.SourceId}'."));
        // The overview of a merged source is in time space.
        if (source is MergedSource && request is { Position: { } milliseconds, Id: null })
            request = request with { Position = null, Time = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(milliseconds, 0, 253_402_300_799_999)) };

        LogQueryMatcher matcher;
        LogQuery query;
        try
        {
            query = request.Query?.ToLogQuery() ?? LogQuery.All;
            matcher = LogQueryMatcher.Create(query);
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(Empty(e.Message));
        }

        var anchor = request switch
        {
            { Id: { } id } => source.Find(id),
            { Position: { } position } => FindFrom(source, matcher, position - 1, cancellationToken)
                ?? source.ReadBefore(position, 1, matcher, cancellationToken).Entries.LastOrDefault(),
            { Time: { } time } => FindFrom(source, LogQueryMatcher.Create(query with { From = query.From is { } f && f > time ? f : time }), 0, cancellationToken)
                ?? source.ReadBefore(null, 1, matcher, cancellationToken).Entries.LastOrDefault(),
            _ => source.ReadBefore(null, 1, matcher, cancellationToken).Entries.LastOrDefault(),
        };
        if (anchor is null)
            return Task.FromResult(Empty(ErrorOf(source)) with { Version = source.Version });

        var half = Math.Clamp(request.Count, 2, GetEntries.MaxCount) / 2;
        var older = source.ReadBefore(anchor.Id, half, matcher, cancellationToken);
        var newer = source.ReadAfter(anchor.Id, half, matcher, cancellationToken);

        var entries = older.Entries.Append(anchor).Concat(newer.Entries).Select(EntryRow.Of(source)).ToArray();
        return Task.FromResult(new AroundPage(
            entries, anchor.Id, older.HasMore, newer.HasMore,
            older.Cursor ?? entries[0].Id, newer.Cursor ?? entries[^1].Id,
            ErrorOf(source), source.Version));
    }

    /// <summary>
    /// The first matching entry with an id above <paramref name="afterId"/>. A file source may stop after its scan
    /// budget, so the search continues from its cursor until it finds an entry or reaches the end.
    /// </summary>
    private static LogEntry? FindFrom(ILogSource source, LogQueryMatcher matcher, long afterId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = source.ReadAfter(afterId, 1, matcher, cancellationToken);
            if (result.Entries.Count > 0)
                return result.Entries[0];
            if (!result.HasMore || result.Cursor is not { } cursor || cursor <= afterId)
                return null;
            afterId = cursor;
        }
    }
    internal static string? ErrorOf(ILogSource source) => source switch
    {
        FileLogSource file => file.Error,
        MergedSource merged => merged.Error,
        _ => null,
    };

    private static AroundPage Empty(string? error) => new([], null, false, false, null, null, error);
}
