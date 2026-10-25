using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;

namespace Kododo.MonitorWay.Sources;

/// <summary>
/// A place MonitorWay reads entries from: the in-memory buffer or a log file.
/// </summary>
/// <remarks>
/// Entry ids grow in the order entries were written, so they double as cursors: a page continues
/// with the entries older than the first id it has (<see cref="ReadBefore"/>) or newer than the last
/// (<see cref="ReadAfter"/>).
/// </remarks>
internal interface ILogSource
{
    string Id { get; }
    string Name { get; }
    string Kind { get; }

    /// <summary>Heading the source is listed under in the UI, e.g. the directory or the service; <c>null</c> for none.</summary>
    string? Group => null;

    /// <summary>Changes when ids issued before are no longer valid, e.g. when a file was truncated or replaced.</summary>
    long Version { get; }

    /// <summary>
    /// Returns up to <paramref name="count"/> matching entries with an id lower than <paramref name="beforeId"/>
    /// (the newest ones when it is <c>null</c>), in ascending order.
    /// </summary>
    ReadResult ReadBefore(long? beforeId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="count"/> matching entries with an id higher than <paramref name="afterId"/>, in ascending order.</summary>
    ReadResult ReadAfter(long afterId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken);

    LogEntry? Find(long id);
}

/// <param name="Entries">Matching entries in ascending order.</param>
/// <param name="HasMore">Whether more entries may follow in the read direction.</param>
/// <param name="Cursor">
/// Where the read stopped, when it differs from the last returned entry: a source that limits how much
/// it scans per call returns the position to pass as <c>before</c> or <c>after</c> to continue.
/// </param>
internal sealed record ReadResult(IReadOnlyList<LogEntry> Entries, bool HasMore, long? Cursor = null)
{
    public static ReadResult Empty { get; } = new([], false);
}
