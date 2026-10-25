using System.Globalization;
using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Kododo.MonitorWay.UI.DTO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kododo.MonitorWay.UI.API;

/// <summary>
/// Server-sent events with entries appended to a source.
/// </summary>
/// <remarks>
/// Query string: <c>source</c>, <c>after</c> (last id the client has), and the filter
/// <c>levels</c> (comma separated), <c>text</c>, <c>regex</c>, <c>caseSensitive</c>, <c>from</c>, <c>to</c>,
/// and <c>category</c> / <c>excludedCategory</c> repeated once per category.
/// Events: <c>entry</c> with an <see cref="EntryRow"/>; <c>gap</c> with the number of entries skipped
/// because the client did not keep up; <c>reset</c> when the source's earlier ids became invalid
/// (a file was truncated or replaced) and the client should load the source again.
/// </remarks>
internal static class LiveStream
{
    private const int BatchSize = 500;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FilePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(HttpContext context)
    {
        var request = context.Request.Query;
        var sourceId = request["source"].ToString() is { Length: > 0 } id ? id : MemorySource.SourceId;
        var sources = context.RequestServices.GetRequiredService<LogSources>();
        if (sources.Find(sourceId) is not { } source)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        LogQueryMatcher matcher;
        try
        {
            matcher = LogQueryMatcher.Create(ParseQuery(request).ToLogQuery());
        }
        catch (ArgumentException e)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(e.Message, context.RequestAborted);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var after = long.TryParse(request["after"], CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        try
        {
            if (source is MemorySource memory)
                await StreamMemoryAsync(context, memory, matcher, after);
            else if (source is MergedSource merged)
                await StreamMergedAsync(context, merged, () => sources.Find(sourceId), matcher, after);
            else
                await StreamPolledAsync(context, source, matcher, after);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected.
        }
    }

    /// <summary>Pushes entries as they are logged.</summary>
    private static async Task StreamMemoryAsync(HttpContext context, MemorySource source, LogQueryMatcher matcher, long? after)
    {
        var aborted = context.RequestAborted;
        var output = new StringBuilder();
        var lastId = after ?? source.Buffer.GetStats().LastId;
        using var subscription = source.Buffer.Subscribe();

        // Entries appended between the client's last read and the subscription.
        ReadResult backlog;
        do
        {
            backlog = source.ReadAfter(lastId, BatchSize, matcher, aborted);
            foreach (var entry in backlog.Entries)
                AppendEntry(output, entry);
            if (backlog.Entries.Count > 0)
                lastId = backlog.Entries[^1].Id;
            await FlushAsync(context, output, aborted);
        } while (backlog.HasMore);

        while (!aborted.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            heartbeat.CancelAfter(HeartbeatInterval);
            try
            {
                if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token))
                    return;
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                output.Append(": ping\n\n");
                await FlushAsync(context, output, aborted);
                continue;
            }

            var dropped = subscription.TakeDropped();
            if (dropped > 0)
                output.Append("event: gap\ndata: ").Append(dropped).Append("\n\n");

            while (subscription.Reader.TryRead(out var entry))
            {
                if (entry.Id <= lastId)
                    continue;
                lastId = entry.Id;
                if (matcher.Matches(entry))
                    AppendEntry(output, entry);
            }
            await FlushAsync(context, output, aborted);
        }
    }

    /// <summary>
    /// Polls each member of a merged source from its own position, so an entry one of them writes a moment after the
    /// others still arrives. Entries found in one poll are sent in time order. The members are looked up again on each
    /// poll: a new rotated file or replica changes the source's version, and the client loads it again.
    /// </summary>
    private static async Task StreamMergedAsync(
        HttpContext context, MergedSource source, Func<ILogSource?> current, LogQueryMatcher matcher, long? after)
    {
        var aborted = context.RequestAborted;
        var output = new StringBuilder();
        var version = source.Version;
        var positions = source.PositionsAfter(after, aborted);
        // The members are fixed while the version is, and so are their names.
        var names = source.MemberNames();
        var sinceOutput = TimeSpan.Zero;

        using var timer = new PeriodicTimer(FilePollInterval);
        do
        {
            if (current() is not { } now || now.Version != version)
            {
                output.Append("event: reset\ndata: ").Append(current()?.Version ?? 0).Append("\n\n");
                await FlushAsync(context, output, aborted);
                return;
            }

            var found = new List<(LogEntry Entry, int Member)>();
            for (var i = 0; i < source.Members.Count; i++)
            {
                if (source.Members[i] is not { } member || positions[i] is not { } position)
                    continue;
                ReadResult result;
                do
                {
                    result = member.ReadAfter(position, BatchSize, matcher, aborted);
                    found.AddRange(result.Entries.Select(e => (e, i)));
                    position = result.Cursor ?? (result.Entries.Count > 0 ? result.Entries[^1].Id : position);
                } while (result.HasMore && !aborted.IsCancellationRequested);
                positions[i] = position;
            }
            foreach (var (entry, member) in found
                .OrderBy(f => f.Entry.Timestamp ?? DateTimeOffset.MinValue).ThenBy(f => f.Member).ThenBy(f => f.Entry.Id))
            {
                var id = MergedSource.Encode(member, entry.Id);
                AppendEntry(output, entry with { Id = id }, names(id));
            }

            sinceOutput = output.Length > 0 ? TimeSpan.Zero : sinceOutput + FilePollInterval;
            if (sinceOutput >= HeartbeatInterval)
            {
                output.Append(": ping\n\n");
                sinceOutput = TimeSpan.Zero;
            }
            await FlushAsync(context, output, aborted);
        } while (await timer.WaitForNextTickAsync(aborted));
    }

    /// <summary>Polls a source that cannot push, such as a file being appended to.</summary>
    private static async Task StreamPolledAsync(HttpContext context, ILogSource source, LogQueryMatcher matcher, long? after)
    {
        var aborted = context.RequestAborted;
        var output = new StringBuilder();
        var version = source.Version;
        var position = after ?? source.ReadAfter(long.MaxValue, 1, matcher, aborted).Cursor ?? 0;
        var sinceOutput = TimeSpan.Zero;

        using var timer = new PeriodicTimer(FilePollInterval);
        do
        {
            if (source.Version != version)
            {
                output.Append("event: reset\ndata: ").Append(source.Version).Append("\n\n");
                await FlushAsync(context, output, aborted);
                return;
            }

            ReadResult result;
            do
            {
                result = source.ReadAfter(position, BatchSize, matcher, aborted);
                foreach (var entry in result.Entries)
                    AppendEntry(output, entry);
                position = result.Cursor ?? (result.Entries.Count > 0 ? result.Entries[^1].Id : position);
            } while (result.HasMore && !aborted.IsCancellationRequested);

            sinceOutput = output.Length > 0 ? TimeSpan.Zero : sinceOutput + FilePollInterval;
            if (sinceOutput >= HeartbeatInterval)
            {
                output.Append(": ping\n\n");
                sinceOutput = TimeSpan.Zero;
            }
            await FlushAsync(context, output, aborted);
        } while (await timer.WaitForNextTickAsync(aborted));
    }

    private static Query ParseQuery(IQueryCollection query) => new(
        Levels: query["levels"].ToString() is { Length: > 0 } levels ? levels.Split(',') : null,
        Text: query["text"].ToString() is { Length: > 0 } text ? text : null,
        Regex: bool.TryParse(query["regex"], out var regex) && regex,
        CaseSensitive: bool.TryParse(query["caseSensitive"], out var caseSensitive) && caseSensitive,
        From: DateTimeOffset.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from) ? from : null,
        To: DateTimeOffset.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to) ? to : null,
        Categories: query["category"] is { Count: > 0 } categories ? categories.OfType<string>().ToArray() : null,
        ExcludedCategories: query["excludedCategory"] is { Count: > 0 } excluded ? excluded.OfType<string>().ToArray() : null);

    private static void AppendEntry(StringBuilder output, LogEntry entry, string? member = null) =>
        output.Append("event: entry\ndata: ").Append(JsonSerializer.Serialize(EntryRow.From(entry, member), Json)).Append("\n\n");

    private static async Task FlushAsync(HttpContext context, StringBuilder output, CancellationToken cancellationToken)
    {
        if (output.Length > 0)
        {
            await context.Response.WriteAsync(output.ToString(), cancellationToken);
            output.Clear();
        }
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
