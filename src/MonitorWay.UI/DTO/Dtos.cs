using System.ComponentModel;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.UI.DTO;

internal sealed record Source(string Id, string Name, string Kind, string? Group);

internal sealed record Query(
    [property: Description("Levels to include: Trace, Debug, Information, Warning, Error, Critical. All when left out.")] string[]? Levels = null,
    [property: Description("Search: words must all occur (message, category, exception or property values); \"a phrase\"; " +
                           "-word excludes; message:, category:, exception: search one field; has:exception; trace:<id> for one request; " +
                           "Name:value matches a property (* is a wildcard), e.g. OrderId:1008.")] string? Text = null,
    [property: Description("Treat text as a regular expression instead.")] bool Regex = false,
    [property: Description("Match the case of text.")] bool CaseSensitive = false,
    [property: Description("Only entries at or after this moment (ISO 8601).")] DateTimeOffset? From = null,
    [property: Description("Only entries at or before this moment (ISO 8601).")] DateTimeOffset? To = null,
    [property: Description("Only these categories, whole names as in the list of categories.")] string[]? Categories = null,
    [property: Description("Leave out these categories, whole names.")] string[]? ExcludedCategories = null)
{
    /// <exception cref="ArgumentException">A level name is not valid.</exception>
    public LogQuery ToLogQuery() => new(
        Levels?.Select(ParseLevel).ToArray(), Text, Regex, CaseSensitive, From, To, Categories, ExcludedCategories);

    private static LogLevel ParseLevel(string level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{level}' is not a log level.");
}

/// <param name="Member">
/// In a view of several sources, the one the entry comes from; <c>null</c> otherwise, and for the rotated files of one log.
/// </param>
/// <param name="SpanId">The span the entry was logged in, which places it in its trace.</param>
internal sealed record EntryRow(
    long Id,
    DateTimeOffset? Timestamp,
    string Level,
    string? Category,
    string Message,
    bool HasException,
    string? TraceId,
    string? Member = null,
    string? SpanId = null)
{
    public static EntryRow From(LogEntry entry, string? member = null) => new(
        entry.Id, entry.Timestamp, entry.Level.ToString(), entry.Category, entry.Message, entry.Exception is not null,
        entry.TraceId ?? entry.Properties.FirstOrDefault(p => p.Key == "TraceId").Value, member,
        entry.SpanId ?? entry.Properties.FirstOrDefault(p => p.Key == "SpanId").Value);

    /// <summary>Rows of a source's entries, each with the member it comes from when the source merges several.</summary>
    public static Func<LogEntry, EntryRow> Of(ILogSource source)
    {
        var members = (source as MergedSource)?.MemberNames();
        return members is null ? entry => From(entry) : entry => From(entry, members(entry.Id));
    }
}

/// <param name="AnchorId">The entry the window is centred on; <c>null</c> when the source has no entries.</param>
/// <param name="OlderCursor">Where to continue towards older entries (pass as <c>before</c>).</param>
/// <param name="NewerCursor">Where to continue towards newer entries (pass as <c>after</c>).</param>
internal sealed record AroundPage(
    EntryRow[] Entries,
    long? AnchorId,
    bool HasOlder,
    bool HasNewer,
    long? OlderCursor,
    long? NewerCursor,
    string? Error = null,
    long Version = 0);

/// <param name="Cursor">Where to continue when the source stopped scanning before finding enough entries.</param>
/// <param name="Version">Changes when earlier ids of the source became invalid (file truncated or replaced).</param>
internal sealed record EntriesPage(EntryRow[] Entries, bool HasMore, string? Error = null, long? Cursor = null, long Version = 0);

internal sealed record Property(string Key, string? Value);

/// <param name="ErrorFingerprint">The error group of the entry's exception.</param>
internal sealed record EntryDetail(
    long Id,
    DateTimeOffset? Timestamp,
    string Level,
    string? Category,
    string Message,
    string? MessageTemplate,
    string? Exception,
    Property[] Properties,
    string? TraceId,
    string? SpanId,
    string? ErrorFingerprint = null)
{
    public static EntryDetail From(LogEntry entry) => new(
        entry.Id, entry.Timestamp, entry.Level.ToString(), entry.Category, entry.Message, entry.MessageTemplate,
        entry.Exception, entry.Properties.Select(p => new Property(p.Key, p.Value)).ToArray(),
        entry.TraceId, entry.SpanId,
        entry.Exception is null ? null : ErrorLink.Of(entry.ExceptionType, entry.Exception, null));
}

/// <param name="Pool">Index of the retention pool holding the level, or -1 for sources without pools.</param>
internal sealed record LevelStats(string Level, long Count, int Pool);

internal sealed record FileDetails(
    string Path,
    long Length,
    long IndexedBytes,
    bool Indexing,
    string? Format,
    string? Error);

internal sealed record Pool(
    string[] Levels,
    int Count,
    long Bytes,
    DateTimeOffset? Oldest,
    long Evicted,
    int? MaxEntries,
    long? MaxBytes,
    double? MaxAgeSeconds)
{
    public static Pool From(PoolStats stats) => new(
        stats.Levels.Select(l => l.ToString()).ToArray(), stats.Count, stats.Bytes, stats.Oldest, stats.Evicted,
        stats.MaxEntries, stats.MaxBytes, stats.MaxAge?.TotalSeconds);
}

/// <param name="TotalBytes">Memory used by the buffer; 0 for files.</param>
/// <param name="File">Set for file sources.</param>
internal sealed record Stats(
    LevelStats[] Levels,
    Pool[] Pools,
    long TotalBytes,
    long MaxTotalBytes,
    long LastId,
    DateTimeOffset Now,
    long Version = 0,
    FileDetails? File = null);
