using System.ComponentModel;
using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.UI.DTO;
using Kododo.Reiho.AspNetCore.API;

namespace Kododo.MonitorWay.UI.API;

[Description("Lists the exceptions of the application grouped by type and place in the code: count, first and last seen, " +
             "services, operations and the hourly trend of the last 24 hours. Read a single group for its stack trace and occurrences.")]
internal sealed record GetErrors(
    [property: Description("Order: \"recent\" (last seen first, the default) or \"count\" (most frequent first).")] string Sort = "recent",
    [property: Description("How many groups to return, at most 500.")] int Count = GetErrors.DefaultCount,
    [property: Description("Filters; all that are set must match.")] ErrorQueryDto? Query = null) : IRequest<ErrorsPage>
{
    public const int DefaultCount = 30;
    public const int MaxCount = 500;
}

internal sealed class GetErrorsHandler(MemoryErrorStore store) : IRequestHandler<GetErrors, ErrorsPage>
{
    public Task<ErrorsPage> HandleAsync(GetErrors request, CancellationToken cancellationToken)
    {
        ErrorQueryMatcher matcher;
        try
        {
            matcher = request.Query is null ? ErrorQueryMatcher.All : ErrorQueryMatcher.Create(request.Query.ToErrorQuery());
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(new ErrorsPage([], e.Message));
        }

        var count = request.Count is > 0 and <= GetErrors.MaxCount ? request.Count : GetErrors.DefaultCount;
        var byCount = string.Equals(request.Sort, "count", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new ErrorsPage(store.Read(matcher, byCount, count).Select(ErrorRow.From).ToArray()));
    }
}

[Description("Reads one error group: the stack trace of its latest occurrence, the frames it is recognized by, the hourly trend " +
             "of the last 7 days and the latest occurrences with their trace ids (read the trace, or the log entries with trace:<id>).")]
internal sealed record GetError(
    [property: Description("Fingerprint of the group, as in the list of errors.")] string Fingerprint) : IRequest<ErrorDetail?>;

internal sealed class GetErrorHandler(MemoryErrorStore store) : IRequestHandler<GetError, ErrorDetail?>
{
    public Task<ErrorDetail?> HandleAsync(GetError request, CancellationToken cancellationToken)
    {
        var view = store.Find(request.Fingerprint.Trim().ToLowerInvariant());
        return Task.FromResult(view is null ? null : ErrorDetail.From(view));
    }
}

[Description("Tells whether errors are collected and how many groups and occurrences are kept.")]
internal sealed record GetErrorStats : IRequest<ErrorStats>;

internal sealed class GetErrorStatsHandler(MemoryErrorStore store) : IRequestHandler<GetErrorStats, ErrorStats>
{
    public Task<ErrorStats> HandleAsync(GetErrorStats request, CancellationToken cancellationToken)
    {
        var (groups, occurrences) = store.GetStats();
        return Task.FromResult(new ErrorStats(store.IsExportedTo, groups, occurrences, store.MaxGroups, store.MaxAge?.TotalSeconds));
    }
}
