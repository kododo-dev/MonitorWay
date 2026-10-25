using System.ComponentModel;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

[Description("Lists the stored traces (one per request or operation), newest first: the root span's name, duration in " +
             "milliseconds, HTTP status code, services, span and error counts. Failed and slow traces are kept longer than the " +
             "rest. Without before it returns the newest ones; to page back pass the id of the last trace as before. " +
             "Read a single trace for its spans.")]
internal sealed record GetTraces(
    [property: Description("Return traces older than this id.")] long? Before = null,
    [property: Description("How many traces to return, at most 500. Keep it small, e.g. 20, and narrow the query instead.")] int Count = GetTraces.DefaultCount,
    [property: Description("Filters; all that are set must match.")] TraceQueryDto? Query = null) : IRequest<TracesPage>
{
    public const int DefaultCount = 30;
    public const int MaxCount = 500;
}

internal sealed class GetTracesHandler(MemoryTraceStore store) : IRequestHandler<GetTraces, TracesPage>
{
    public Task<TracesPage> HandleAsync(GetTraces request, CancellationToken cancellationToken)
    {
        TraceQueryMatcher matcher;
        try
        {
            matcher = request.Query is null ? TraceQueryMatcher.All : TraceQueryMatcher.Create(request.Query.ToTraceQuery());
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(new TracesPage([], false, e.Message));
        }

        var count = request.Count is > 0 and <= GetTraces.MaxCount ? request.Count : GetTraces.DefaultCount;
        var lastId = store.GetStats().LastId;
        var traces = store.ReadBefore(request.Before, count, matcher, out var hasMore);
        return Task.FromResult(new TracesPage(traces.Select(TraceRow.From).ToArray(), hasMore, null, lastId));
    }
}

[Description("Reads one trace with all its spans: names, kinds, start (offsetMs from the start of the trace) and duration in " +
             "milliseconds, status, attributes and events (exceptions with their stack traces among them). Its logs are the log " +
             "entries of the source memory with trace:<traceId>.")]
internal sealed record GetTrace(
    [property: Description("W3C trace id, 32 hexadecimal digits, as in the list of traces or the traceId of a log entry.")] string TraceId)
    : IRequest<TraceDetail?>;

internal sealed class GetTraceHandler(MemoryTraceStore store) : IRequestHandler<GetTrace, TraceDetail?>
{
    public Task<TraceDetail?> HandleAsync(GetTrace request, CancellationToken cancellationToken)
    {
        var trace = store.Find(request.TraceId.Trim().ToLowerInvariant());
        return Task.FromResult(trace is null ? null : TraceDetail.From(trace));
    }
}

[Description("Tells whether traces are collected and how many are kept: per retention pool (errors, slow, other) and in total.")]
internal sealed record GetTraceStats : IRequest<TraceStats>;

internal sealed class GetTraceStatsHandler(MemoryTraceStore store, TimeProvider time) : IRequestHandler<GetTraceStats, TraceStats>
{
    public Task<TraceStats> HandleAsync(GetTraceStats request, CancellationToken cancellationToken)
    {
        var stats = store.GetStats();
        return Task.FromResult(new TraceStats(
            store.IsExportedTo, stats.Pools.Select(DTO.TracePool.From).ToArray(), stats.Pending, stats.TotalBytes,
            stats.MaxTotalBytes, store.SlowThreshold.TotalMilliseconds, stats.LastId, time.GetUtcNow()));
    }
}
